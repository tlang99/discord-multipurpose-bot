using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.Modules.Music;

/// <summary>
/// DJ mode: when the queue runs out, the chat model picks a song like the ones played lately, and it's looked up on
/// YouTube, so the music keeps going instead of the bot leaving. A server turns it on (ServerFeature.MusicDj).
/// </summary>
public class DjPicker
{
    /// <summary>How many recent songs the model is shown to go on.</summary>
    private const int RecentShown = 8;

    /// <summary>Tries at a song before giving up (one may be a repeat, or not found).</summary>
    private const int Tries = 3;

    private const string Prompt =
        "You pick the next song for a group of friends' music bot, when their queue has run out. You're shown the " +
        "songs they played most recently, newest first. Suggest ONE real, well-known song in the same style or " +
        "mood that isn't one of them, as \"Artist - Title\" for a YouTube search. Don't explain.";

    private const string Schema =
        """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    private readonly IChatService _chat;
    private readonly MusicService _music;
    private readonly IGuildSettingsStore _settings;
    private readonly ChatOptions _chatOptions;
    private readonly ILogger<DjPicker> _logger;

    public DjPicker(IChatService chat, MusicService music, IGuildSettingsStore settings,
        IOptions<ChatOptions> chatOptions, ILogger<DjPicker> logger)
    {
        _chat = chat;
        _music = music;
        _settings = settings;
        _chatOptions = chatOptions.Value;
        _logger = logger;
    }

    /// <summary>A song like the recent ones in <paramref name="history"/>, ready to play, picked by
    /// <paramref name="dj"/> (the bot), or null if none could be found (the model's off or down, or nothing new
    /// turned up).</summary>
    public async Task<QueuedSong?> PickAsync(ulong guildId, IReadOnlyList<QueuedSong> history, (ulong Id, string Name) dj,
        CancellationToken cancellationToken = default)
    {
        if (!_chatOptions.IsEnabled || history.Count == 0)
        {
            return null;
        }

        var recent = history.Take(RecentShown).Select(s => s.Title).ToList();
        var settings = await _settings.GetAsync(guildId);
        var suggested = new List<string>();
        for (var attempt = 0; attempt < Tries; attempt++)
        {
            try
            {
                // Told what it already suggested this time round too, so a retry isn't the same song.
                var asked = "Played lately:\n" + string.Join('\n', recent) +
                            (suggested.Count == 0 ? "" : "\n\nAlready suggested (pick another):\n" +
                                                         string.Join('\n', suggested));
                using var json = JsonDocument.Parse(await _chat.CompleteJsonAsync(Prompt, asked, Schema,
                    cancellationToken));
                var query = json.RootElement.GetProperty("query").GetString()?.Trim();
                if (string.IsNullOrEmpty(query) || MusicService.CheckQuery(query) is not null)
                {
                    continue;
                }

                suggested.Add(query);
                var found = await _music.FindTracksAsync(query.Length > 200 ? query[..200] : query, guildId);
                var track = found.Tracks.FirstOrDefault(t => !t.IsLiveStream);
                // Not a repeat of something played lately, and not a title the server's word filter blocks: it ends
                // up on the menu for everyone.
                if (track is null || history.Any(s => s.Track == track.ToString()) ||
                    TextFilter.FindViolation(track.Title, settings) is not null)
                {
                    continue;
                }

                return new QueuedSong
                {
                    Track = track.ToString(),
                    Title = track.Title,
                    Uri = track.Uri?.ToString(),
                    Duration = track.Duration,
                    PickerId = dj.Id,
                    PickerName = dj.Name,
                };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // An answer that isn't the JSON asked for: ask again.
                _logger.LogDebug(ex, "DJ mode got an answer it couldn't read in guild {GuildId}.", guildId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The model or the song lookup is down: trying again now won't help, and the music just stops.
                _logger.LogInformation(ex, "DJ mode couldn't pick a song in guild {GuildId}.", guildId);
                return null;
            }
        }

        return null;
    }
}
