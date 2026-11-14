using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.Modules.Music;

/// <summary>A song DJ mode picked, ready to play.</summary>
/// <param name="LikedBy">Whose liked song it is, when it's one played again rather than a new pick.</param>
public record DjPick(QueuedSong Song, ulong? LikedBy = null);

/// <summary>
/// DJ mode: when the queue runs out, the chat model picks a song for the people listening, and it's looked up on
/// YouTube, so the music keeps going instead of the bot leaving. It goes by the songs people played lately, what the
/// listeners say they like and the songs they've liked, and steers away from its picks that got skipped (see
/// <see cref="DjRules"/>). Now and then it plays one of the listeners' liked songs again instead. A server turns it
/// on (ServerFeature.MusicDj).
/// </summary>
public class DjPicker
{
    /// <summary>Tries at a song before giving up (one may be a repeat, or not found).</summary>
    private const int Tries = 3;

    private const string Prompt =
        "You pick the next song for a group of friends' music bot, when their queue has run out. You're shown the " +
        "songs they played lately, and may be shown what the people listening like, songs they've liked, and your " +
        "earlier picks they skipped. Suggest ONE real, well-known song that suits the people listening and isn't " +
        "one already shown, as \"Artist - Title\" for a YouTube search. Don't explain.";

    private const string Schema =
        """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    private readonly IChatService _chat;
    private readonly MusicService _music;
    private readonly IGuildSettingsStore _settings;
    private readonly ChatOptions _chatOptions;
    private readonly ILogger<DjPicker> _logger;

    /// <summary>Most a song can ask to keep the model loaded for: a long song shouldn't hold memory all night.</summary>
    private static readonly TimeSpan LongestHold = TimeSpan.FromMinutes(20);

    /// <summary>Past the end of the song: time for the pick itself, and a song ending a little late.</summary>
    private static readonly TimeSpan HoldSpare = TimeSpan.FromMinutes(2);

    /// <summary>When each server's hold on the model ends (see <see cref="WarmUpAsync"/>).</summary>
    private readonly ConcurrentDictionary<ulong, DateTime> _holds = new();

    public DjPicker(IChatService chat, MusicService music, IGuildSettingsStore settings,
        IOptions<ChatOptions> chatOptions, ILogger<DjPicker> logger)
    {
        _chat = chat;
        _music = music;
        _settings = settings;
        _chatOptions = chatOptions.Value;
        _logger = logger;
    }

    /// <summary>A song for the people in <paramref name="listeners"/>, ready to play, picked by <paramref name="dj"/>
    /// (the bot), or null if none could be found (the model's off, or it's down or nothing new turned up and none of
    /// the listeners have liked songs to fall back on).</summary>
    /// <param name="state">The server's saved music: the history, tastes, likes and skipped picks.</param>
    public async Task<DjPick?> PickAsync(ulong guildId, MusicQueueState state, IReadOnlySet<ulong> listeners,
        (ulong Id, string Name) dj, Random? random = null, CancellationToken cancellationToken = default)
    {
        if (!_chatOptions.IsEnabled || state.History.Count == 0)
        {
            return null;
        }

        random ??= Random.Shared;
        var settings = await _settings.GetAsync(guildId);
        // Liked songs are shown on the menu when they play, so they follow the word filter as it is now.
        var liked = DjRules.LikedToPlay(state, listeners, random)
            .Where(l => TextFilter.FindViolation(l.Song.Title, settings) is null)
            .ToList();
        DjPick? FromLikes() => liked.Count == 0
            ? null
            : new DjPick(MusicQueueRules.Copy(liked[0].Song, dj.Id, dj.Name), liked[0].LikedBy);

        if (random.NextDouble() < DjRules.LikedShare && FromLikes() is { } again)
        {
            return again;
        }

        var seeds = DjRules.Seeds(state.History, dj.Id);
        var tastes = DjRules.TastesOf(state, listeners);
        var examples = liked.Take(DjRules.LikesShown).Select(l => l.Song.Title).ToList();
        var suggested = new List<string>();
        for (var attempt = 0; attempt < Tries; attempt++)
        {
            try
            {
                var asked = DjRules.Ask(seeds, tastes, examples, state.DjSkipped, suggested);
                // An answer that isn't the JSON asked for gives no query: ask again.
                var query = ReadQuery(await _chat.CompleteJsonAsync(Prompt, asked, Schema, cancellationToken));
                if (string.IsNullOrEmpty(query) || MusicService.CheckQuery(query) is not null)
                {
                    continue;
                }

                suggested.Add(query);
                var found = await _music.FindTracksAsync(query.Length > 200 ? query[..200] : query, guildId);
                var track = found.Tracks.FirstOrDefault(t => !t.IsLiveStream);
                var song = track is null
                    ? null
                    : new QueuedSong
                    {
                        Track = track.ToString(),
                        Title = track.Title,
                        Uri = track.Uri?.ToString(),
                        Duration = track.Duration,
                        PickerId = dj.Id,
                        PickerName = dj.Name,
                    };
                // Not a repeat of something played lately, and not a title the server's word filter blocks: it ends
                // up on the menu for everyone.
                if (song is null || state.History.Any(s => DjRules.SameSong(s, song)) ||
                    TextFilter.FindViolation(song.Title, settings) is not null)
                {
                    continue;
                }

                return new DjPick(song);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The model or the song lookup is down: trying again now won't help. A liked song keeps the music
                // going if there is one; otherwise it just stops.
                _logger.LogInformation(ex, "DJ mode couldn't pick a song in guild {GuildId}.", guildId);
                return FromLikes();
            }
        }

        return FromLikes();
    }

    /// <summary>
    /// Loads the chat model and keeps it loaded until just after the song playing ends (<paramref name="left"/> from
    /// now, at most <see cref="LongestHold"/>), so a pick then doesn't wait for the model to load first. Loading takes
    /// seconds on its own and can take a minute or more while a game has the graphics card busy, long enough for the
    /// music to stop. Replaces the server's last hold. Never fails.
    /// </summary>
    public Task WarmUpAsync(ulong guildId, TimeSpan left, CancellationToken cancellationToken = default)
    {
        if (!CanPick)
        {
            return Task.CompletedTask;
        }

        var hold = left + HoldSpare < LongestHold ? left + HoldSpare : LongestHold;
        _holds[guildId] = DateTime.UtcNow + hold;
        return _chat.WarmUpAsync(cancellationToken, LongestHoldLeft());
    }

    /// <summary>The server no longer needs the model kept loaded (the song that started has others queued after it,
    /// is a live stream, or DJ mode's off; or the music stopped): its hold ends, so memory isn't held for a pick that
    /// won't come. Other servers' holds stay. Never fails.</summary>
    public Task ReleaseAsync(ulong guildId)
        => _holds.TryRemove(guildId, out _) ? _chat.WarmUpAsync(keepLoadedFor: LongestHoldLeft()) : Task.CompletedTask;

    /// <summary>Whether DJ mode can pick at all: the chatbot's set up.</summary>
    public bool CanPick => _chatOptions.IsEnabled;

    /// <summary>How much longer the longest hold still running lasts (zero if none), forgetting ones that are over.</summary>
    private TimeSpan LongestHoldLeft()
    {
        var now = DateTime.UtcNow;
        var longest = TimeSpan.Zero;
        foreach (var (guildId, until) in _holds)
        {
            if (until <= now)
            {
                _holds.TryRemove(new KeyValuePair<ulong, DateTime>(guildId, until));
            }
            else if (until - now > longest)
            {
                longest = until - now;
            }
        }

        return longest;
    }

    /// <summary>The search the model's answer asks for, or null if the answer isn't the JSON asked for.</summary>
    private static string? ReadQuery(string answer)
    {
        try
        {
            using var json = JsonDocument.Parse(answer);
            return json.RootElement.GetProperty("query").GetString()?.Trim();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
