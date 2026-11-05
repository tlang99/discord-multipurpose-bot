using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Clients;
using Lavalink4NET.Players;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Modules.Voice;

namespace ModBot.Modules.Music;

/// <summary>The songs a search or link found, or why there aren't any.</summary>
/// <param name="PlaylistName">Set when the link was a playlist (then <paramref name="Tracks"/> is all of it).</param>
public record TrackLookup(IReadOnlyList<LavalinkTrack> Tracks, string? PlaylistName, string? Error)
{
    public bool IsPlaylist => PlaylistName is not null;
}

/// <summary>
/// The Lavalink side of music: finding songs (YouTube only), joining a voice channel, and the per-server lock that
/// keeps changes to what's playing one at a time. What plays when is <see cref="MusicQueueService"/>'s job.
/// </summary>
public class MusicService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly PlayerFactory<MusicPlayer, MusicPlayerOptions> PlayerFactoryInstance =
        PlayerFactory.Create<MusicPlayer, MusicPlayerOptions>(properties => new MusicPlayer(properties));

    /// <summary>Hosts play requests accept links from (plus their subdomains, e.g. www., m., music.youtube.com).</summary>
    private static readonly string[] AllowedLinkHosts = ["youtube.com", "youtu.be"];

    /// <summary>Per-server lock around changing what's playing (starting, skipping, the next turn).</summary>
    private readonly KeyedLock<ulong> _playbackLocks = new();

    private readonly IAudioService _audioService;
    private readonly MusicPlayerEvents _events;
    private readonly MusicOptions _options;
    private readonly VoiceListener _voice;
    private readonly ILogger<MusicService> _logger;

    public MusicService(IAudioService audioService, MusicPlayerEvents events, IOptions<MusicOptions> options,
        VoiceListener voice, ILogger<MusicService> logger)
    {
        _audioService = audioService;
        _events = events;
        _voice = voice;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The server's live player, or null if the bot isn't in voice there.</summary>
    public MusicPlayer? GetPlayer(ulong guildId) => GetPlayer(_audioService, guildId);

    /// <summary>The same, for code that can't depend on MusicService itself (VoiceListener, which MusicService uses):
    /// the one definition of "music has the voice connection".</summary>
    public static MusicPlayer? GetPlayer(IAudioService audioService, ulong guildId)
        => audioService.Players.TryGetPlayer<MusicPlayer>(guildId, out var player) &&
           player is not null && player.State != PlayerState.Destroyed
            ? player
            : null;

    /// <summary>Runs <paramref name="work"/> holding this server's playback lock. Keep it short: other music commands
    /// in the server wait on it, within Discord's 3-second reply window.</summary>
    public Task<T> WithPlaybackLockAsync<T>(ulong guildId, Func<Task<T>> work)
        => _playbackLocks.RunAsync(guildId, work);

    /// <summary>Why <paramref name="query"/> can't be looked up at all (empty, or a link to a site the bot doesn't
    /// play), or null if it can. Cheap, so callers check it before anything slow.</summary>
    public static string? CheckQuery(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return "Tell me what to play.";
        }

        // Only hand Lavalink links to sites it's meant to play, rather than relying on its config alone to refuse
        // the rest.
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            !IsAllowedLinkHost(uri.Host))
        {
            // SoundCloud only serves audio in a format Lavalink can't play yet (lavalink/README.md).
            return HostMatches(uri.Host, "soundcloud.com")
                ? "SoundCloud links don't work right now (the music server can't play SoundCloud's audio format " +
                  "yet). Try searching for the song by name instead - that uses YouTube."
                : "I can only play YouTube links. To find a song from somewhere else, search for it by name instead.";
        }

        return null;
    }

    /// <summary>Looks up a search (top YouTube result) or a YouTube song or playlist link (all of it).</summary>
    public async Task<TrackLookup> FindTracksAsync(string query, ulong guildId)
    {
        query = query.Trim();
        if (CheckQuery(query) is { } problem)
        {
            return new([], null, problem);
        }

        var isLink = Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

        TrackLoadResult result;
        try
        {
            result = await _audioService.Tracks.LoadTracksAsync(query, isLink ? TrackSearchMode.None : TrackSearchMode.YouTube);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Track lookup failed for '{Query}' in guild {GuildId}.", query, guildId);
            return new([], null, "Something went wrong looking that up - try again in a moment. (If it keeps " +
                                 "happening, the music server may be down - let a server admin know.)");
        }

        if (!result.HasMatches)
        {
            return new([], null, result.IsFailed
                ? $"Couldn't load that{(result.Exception?.Message is { } reason ? $": {TextTools.Escape(reason)}" : ".")}"
                : isLink ? "That link isn't a YouTube song or playlist I can play." : "No results found.");
        }

        // A search returns several matches - only the top one is wanted. A playlist link means all of them.
        return result.IsPlaylist
            ? new(result.Tracks.ToList(), result.Playlist?.Name ?? "playlist", null)
            : new([result.Track ?? result.Tracks[0]], null, null);
    }

    /// <summary>Why the bot can't join the user's voice channel to play, or null if it can.</summary>
    public string? GetJoinProblem(SocketGuildUser user)
    {
        if (user.VoiceChannel is not { } voiceChannel)
        {
            return "Join a voice channel first.";
        }

        if (GetPlayer(user.Guild.Id) is { } existing && existing.VoiceChannelId != voiceChannel.Id)
        {
            return $"I'm already playing in <#{existing.VoiceChannelId}> - join that channel instead.";
        }

        var permissions = user.Guild.CurrentUser.GetPermissions(voiceChannel);
        if (!permissions.ViewChannel || !permissions.Connect || !permissions.Speak)
        {
            return $"I need **View Channel**, **Connect**, and **Speak** in {voiceChannel.Mention} to play music there.";
        }

        // Move Members lets the bot join a full channel anyway; without it, joining would just time out.
        if (voiceChannel is { UserLimit: > 0 and var limit } && voiceChannel.ConnectedUsers.Count >= limit &&
            !permissions.MoveMembers)
        {
            return $"{voiceChannel.Mention} is full.";
        }

        return null;
    }

    /// <summary>Gets (or creates and connects) the player for the user's voice channel. Check
    /// <see cref="GetJoinProblem"/> first for a friendlier reason when it can't.</summary>
    /// <param name="announcementChannel">Where "Now playing" goes if this creates the player.</param>
    public async Task<(MusicPlayer? Player, string Error)> JoinPlayerAsync(SocketGuildUser user,
        ITextChannel announcementChannel)
    {
        var playerOptions = new MusicPlayerOptions
        {
            TextChannel = announcementChannel,
            Events = _events,
            SelfDeaf = true,
            ClearQueueOnStop = true,
        };

        // Listening for voice commands holds the server's one voice connection, which music needs to play. Music wins:
        // stop listening and give Discord a moment to see the bot leave before Lavalink joins.
        if (GetPlayer(user.Guild.Id) is null && _voice.IsListening(user.Guild.Id))
        {
            await _voice.StopAsync(user.Guild.Id,
                "🎶 Music's starting, so I've stopped listening for voice commands - I can't do both at once. Ask me " +
                "to listen again once the music's done.");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            var result = await _audioService.Players.RetrieveAsync(user.Guild.Id, user.VoiceChannel?.Id,
                PlayerFactoryInstance, Options.Create(playerOptions),
                new PlayerRetrieveOptions(PlayerChannelBehavior.Join, MemberVoiceStateBehavior.RequireSame),
                timeout.Token);

            if (result.IsSuccess)
            {
                return (result.Player, string.Empty);
            }

            return (null, result.Status switch
            {
                PlayerRetrieveStatus.UserNotInVoiceChannel => "Join a voice channel first.",
                PlayerRetrieveStatus.VoiceChannelMismatch => "I'm playing in a different voice channel - join that one.",
                _ => "Couldn't join your voice channel.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't get a music player for guild {GuildId}. Is Lavalink running at {Url}?",
                user.Guild.Id, _options.LavalinkUrl);
            return (null, "The music server isn't reachable right now - let a server admin know.");
        }
    }

    private static bool IsAllowedLinkHost(string host) => AllowedLinkHosts.Any(allowed => HostMatches(host, allowed));

    /// <summary>True for <paramref name="domain"/> itself or any subdomain of it - but not e.g. "notyoutube.com".</summary>
    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
