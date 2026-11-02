using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Clients;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Modules.Music;

/// <summary>What happened on a play request: the reply to show (text and/or embed).</summary>
/// <param name="SuppressEmbeds">Hide link previews, for replies that link a song.</param>
public record MusicPlayResult(bool Success, string? Text, Embed? Embed = null, bool SuppressEmbeds = false);

/// <summary>The songs a search or link found, or why there aren't any.</summary>
/// <param name="PlaylistName">Set when the link was a playlist (then <paramref name="Tracks"/> is all of it).</param>
public record TrackLookup(IReadOnlyList<LavalinkTrack> Tracks, string? PlaylistName, string? Error)
{
    public bool IsPlaylist => PlaylistName is not null;
}

/// <summary>
/// Playing music: joining the requester's voice channel, finding the song and queueing it. Shared by /play and the
/// chatbot ("play never gonna give you up") so both follow the same rules (YouTube links only, the queue limit, the
/// requester must be in a voice channel). Also owns the per-server queue lock the other music commands take, and the
/// hooks music sessions use to follow what the player does.
/// </summary>
public class MusicService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly PlayerFactory<MusicPlayer, MusicPlayerOptions> PlayerFactoryInstance =
        PlayerFactory.Create<MusicPlayer, MusicPlayerOptions>(properties => new MusicPlayer(properties));

    /// <summary>Hosts play requests accept links from (plus their subdomains, e.g. www., m., music.youtube.com).</summary>
    private static readonly string[] AllowedLinkHosts = ["youtube.com", "youtu.be"];

    /// <summary>Per-server lock around changing the queue (/play, /skip, /remove, /move, /shuffle, /clearqueue, and
    /// music sessions). One small entry per server that has used music; never removed.</summary>
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _queueLocks = new();

    private readonly IAudioService _audioService;
    private readonly MusicPlayerEvents _events;
    private readonly MusicOptions _options;
    private readonly ILogger<MusicService> _logger;

    public MusicService(IAudioService audioService, MusicPlayerEvents events, IOptions<MusicOptions> options,
        ILogger<MusicService> logger)
    {
        _audioService = audioService;
        _events = events;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The configured music channel's name, or empty when music commands work anywhere.</summary>
    public string ChannelName => _options.ChannelName.Trim().TrimStart('#');

    public int MaxQueueLength => _options.MaxQueueLength;

    /// <summary>The configured music channel, or null if the server doesn't have it.</summary>
    public ITextChannel? FindChannel(SocketGuild guild)
        => ChannelName.Length == 0
            ? null
            : guild.TextChannels.FirstOrDefault(c => c.Name.Equals(ChannelName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The server's live player, or null if the bot isn't playing there.</summary>
    public MusicPlayer? GetPlayer(ulong guildId)
        => _audioService.Players.TryGetPlayer<MusicPlayer>(guildId, out var player) &&
           player is not null && player.State != PlayerState.Destroyed
            ? player
            : null;

    /// <summary>Waits for this server's queue lock. Callers must <c>Release()</c> it in a <c>finally</c>, and keep the
    /// locked section short: other music commands in the server wait on it, within Discord's 3-second reply window.</summary>
    public async Task<SemaphoreSlim> AcquireQueueLockAsync(ulong guildId)
    {
        var queueLock = _queueLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await queueLock.WaitAsync();
        return queueLock;
    }

    /// <summary>Plays <paramref name="query"/> (a search or a YouTube link) for <paramref name="user"/>, or adds it to
    /// the queue.</summary>
    /// <param name="announcementChannel">Where "Now playing" announcements go if this starts a new player.</param>
    /// <param name="replyShowsNowPlaying">True when the caller's reply is the announcement (the /play reply in the
    /// music channel): the first song then comes back as an embed and isn't announced a second time.</param>
    public async Task<MusicPlayResult> PlayAsync(SocketGuildUser user, ITextChannel announcementChannel, string query,
        bool replyShowsNowPlaying)
    {
        if (CheckQuery(query) is { } badQuery)
        {
            return new(false, badQuery);
        }

        if (GetJoinProblem(user) is { } problem)
        {
            return new(false, problem);
        }

        var (player, joinError) = await JoinPlayerAsync(user, announcementChannel);
        if (player is null)
        {
            return new(false, joinError);
        }

        var lookup = await FindTracksAsync(query, user.Guild.Id);
        if (lookup.Error is not null)
        {
            return new(false, lookup.Error);
        }

        // One queue change at a time per server from here: otherwise two requests at once could both see room in the
        // queue and together go past the limit, or both think they're starting playback.
        var queueLock = await AcquireQueueLockAsync(user.Guild.Id);
        List<LavalinkTrack> tracks;
        int position;
        try
        {
            // The limit is on upcoming songs (what /queue lists). When nothing is playing, the first song starts
            // straight away rather than waiting in the queue, so there's room for one more.
            var room = _options.MaxQueueLength - player.Queue.Count + (player.CurrentItem is null ? 1 : 0);
            if (room <= 0)
            {
                return new(false, $"The queue is full ({_options.MaxQueueLength} songs). Skip or remove some first.");
            }

            tracks = lookup.Tracks.Take(room).ToList();

            var startsNow = player.CurrentItem is null;
            if (startsNow && replyShowsNowPlaying)
            {
                player.SuppressNextAnnouncement(tracks[0].Identifier);
            }

            try
            {
                position = await player.PlayAsync(tracks[0]);
            }
            catch
            {
                // Otherwise the stale flag would hide the announcement the next time this song is played.
                player.ClearSuppressedAnnouncement();
                throw;
            }

            if (tracks.Count > 1)
            {
                await player.Queue.AddRangeAsync(tracks.Skip(1).Select(t => (ITrackQueueItem)new TrackQueueItem(t)).ToList());
            }
        }
        finally
        {
            queueLock.Release();
        }

        var playlistNote = lookup.IsPlaylist
            ? $"Added **{tracks.Count}** songs from **{Format.Sanitize(lookup.PlaylistName!)}**" +
              (tracks.Count < lookup.Tracks.Count ? $" (the queue limit cut off {lookup.Tracks.Count - tracks.Count})." : ".")
            : null;

        if (position == 0)
        {
            return replyShowsNowPlaying
                ? new(true, playlistNote, MusicFormatting.NowPlayingEmbed(tracks[0], player,
                    $"Now playing - requested by {user.DisplayName}"))
                : new(true, (playlistNote is null ? "" : playlistNote + " ") +
                            $"▶️ Now playing {MusicFormatting.TrackLink(tracks[0])} ({MusicFormatting.Length(tracks[0])}).",
                    SuppressEmbeds: true);
        }

        return new(true, playlistNote ??
            $"Added {MusicFormatting.TrackLink(tracks[0])} ({MusicFormatting.Length(tracks[0])}) to the queue at position **{position}**.",
            SuppressEmbeds: true);
    }

    /// <summary>Why <paramref name="query"/> can't be looked up at all (empty, or a link to a site the bot doesn't
    /// play), or null if it can. Cheap, so callers check it before joining a voice channel.</summary>
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
            return new([], null, "Something went wrong looking that up - try again in a moment. (Is the music server " +
                                 "running?)");
        }

        if (!result.HasMatches)
        {
            return new([], null, result.IsFailed
                ? $"Couldn't load that{(result.Exception?.Message is { } reason ? $": {Format.Sanitize(reason)}" : ".")}"
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
            return $"I'm already playing in <#{existing.VoiceChannelId}> - join that channel to add songs.";
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
                PlayerRetrieveStatus.VoiceChannelMismatch => "I'm playing in a different voice channel - join that one to add songs.",
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
