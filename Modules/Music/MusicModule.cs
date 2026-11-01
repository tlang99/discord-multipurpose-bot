using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.Interactions;
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

/// <summary>
/// Music playback through Lavalink. Anyone can use these, but only from the voice channel the bot is playing in, so
/// people outside the channel can't control what everyone in it is hearing. Moderators (Move Members or Manage
/// Messages) can control it from anywhere.
/// </summary>
[RequireContext(ContextType.Guild)]
public class MusicModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int QueuePageSize = 10;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static readonly PlayerFactory<MusicPlayer, MusicPlayerOptions> PlayerFactoryInstance =
        PlayerFactory.Create<MusicPlayer, MusicPlayerOptions>(properties => new MusicPlayer(properties));

    /// <summary>Hosts /play accepts links from (plus their subdomains, e.g. www., m., music.youtube.com).</summary>
    private static readonly string[] AllowedLinkHosts = ["youtube.com", "youtu.be"];

    /// <summary>Per-server lock around adding to the queue. Static because a module instance is created per command.</summary>
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> EnqueueLocks = new();

    private readonly IAudioService _audioService;
    private readonly MusicOptions _options;
    private readonly ILogger<MusicModule> _logger;

    public MusicModule(IAudioService audioService, IOptions<MusicOptions> options, ILogger<MusicModule> logger)
    {
        _audioService = audioService;
        _options = options.Value;
        _logger = logger;
    }

    public enum LoopMode
    {
        [ChoiceDisplay("Off")] Off,
        [ChoiceDisplay("Current song")] Song,
        [ChoiceDisplay("Whole queue")] Queue,
    }

    [SlashCommand("play", "Plays a song or playlist, or adds it to the queue.")]
    public async Task PlayAsync(
        [Summary(description: "Song to search YouTube for, or a YouTube song/playlist link")] string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            await RespondAsync("Tell me what to play.", ephemeral: true);
            return;
        }

        // Acknowledge first: Discord only allows 3 seconds, and joining voice + searching takes longer than that. (It
        // means the problems below are shown publicly rather than only to the caller.)
        await DeferAsync();

        var isLink = Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

        // Only hand Lavalink links to sites it's meant to play, rather than relying on its config alone to refuse
        // the rest. Checked before joining the voice channel, so a bad link doesn't make the bot join and then fail.
        if (isLink && !IsAllowedLinkHost(uri!.Host))
        {
            // SoundCloud only serves audio in a format Lavalink can't play yet (lavalink/README.md).
            await FollowupAsync(HostMatches(uri.Host, "soundcloud.com")
                ? "SoundCloud links don't work right now (the music server can't play SoundCloud's audio format " +
                  "yet). Try searching for the song by name instead - that uses YouTube."
                : "I can only play YouTube links. To find a song from somewhere else, search for it by name instead.");
            return;
        }

        if (GetJoinProblem() is { } problem)
        {
            await FollowupAsync(problem, allowedMentions: AllowedMentions.None);
            return;
        }

        var player = await JoinPlayerAsync();
        if (player is null)
        {
            return;
        }

        var searchMode = isLink ? TrackSearchMode.None : TrackSearchMode.YouTube;

        TrackLoadResult result;
        try
        {
            result = await _audioService.Tracks.LoadTracksAsync(query, searchMode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Track lookup failed for '{Query}' in guild {GuildId}.", query, Context.Guild.Id);
            await FollowupAsync("Something went wrong looking that up - try again in a moment.");
            return;
        }

        if (!result.HasMatches)
        {
            await FollowupAsync(result.IsFailed
                ? $"Couldn't load that{(result.Exception?.Message is { } reason ? $": {Format.Sanitize(reason)}" : ".")}"
                : isLink ? "That link isn't a YouTube song or playlist I can play." : "No results found.",
                allowedMentions: AllowedMentions.None);
            return;
        }

        // One /play at a time per server from here: otherwise two at once could both see room in the queue and
        // together go past the limit, or both think they're starting playback.
        var enqueueLock = EnqueueLocks.GetOrAdd(Context.Guild.Id, _ => new SemaphoreSlim(1, 1));
        await enqueueLock.WaitAsync();
        List<LavalinkTrack> tracks;
        int position;
        try
        {
            var room = _options.MaxQueueLength - player.Queue.Count;
            if (room <= 0)
            {
                await FollowupAsync($"The queue is full ({_options.MaxQueueLength} songs). Skip or remove some first.");
                return;
            }

            // A search returns several matches - only the top one is wanted. A playlist link means all of them.
            tracks = result.IsPlaylist
                ? result.Tracks.Take(room).ToList()
                : [result.Track ?? result.Tracks[0]];

            var startsNow = player.CurrentItem is null;
            if (startsNow)
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
            enqueueLock.Release();
        }

        var playlistNote = result.IsPlaylist
            ? $"Added **{tracks.Count}** songs from **{Format.Sanitize(result.Playlist?.Name ?? "playlist")}**" +
              (tracks.Count < result.Tracks.Length ? $" (the queue limit cut off {result.Tracks.Length - tracks.Count})." : ".")
            : null;

        if (position == 0)
        {
            await FollowupAsync(text: playlistNote,
                embed: MusicFormatting.NowPlayingEmbed(tracks[0], player, $"Now playing - requested by {Context.User.GlobalName ?? Context.User.Username}"),
                allowedMentions: AllowedMentions.None);
        }
        else
        {
            await FollowupAsync(playlistNote ??
                $"Added {MusicFormatting.TrackLink(tracks[0])} ({MusicFormatting.Length(tracks[0])}) to the queue at position **{position}**.",
                allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
        }
    }

    [SlashCommand("pause", "Pauses the current song.")]
    public async Task PauseAsync()
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        if (player.State == PlayerState.Paused)
        {
            await RespondAsync("Already paused - use `/resume` to continue.", ephemeral: true);
            return;
        }

        await player.PauseAsync();
        await RespondAsync($"⏸️ Paused by {Context.User.Mention}.", allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("resume", "Resumes a paused song.")]
    public async Task ResumeAsync()
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        if (player.State != PlayerState.Paused)
        {
            await RespondAsync("Nothing is paused.", ephemeral: true);
            return;
        }

        await player.ResumeAsync();
        await RespondAsync($"▶️ Resumed by {Context.User.Mention}.", allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("skip", "Skips the current song (or several).")]
    public async Task SkipAsync([Summary(description: "How many songs to skip, including the current one")] [MinValue(1)] int count = 1)
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        var skipped = player.CurrentTrack;
        count = Math.Min(count, player.Queue.Count + 1);
        await player.SkipAsync(count);

        var what = count == 1 ? MusicFormatting.TrackLink(skipped) : $"**{count}** songs";
        var next = player.CurrentTrack is { } current ? $" Now playing {MusicFormatting.TrackLink(current)}." : " The queue is empty.";
        await RespondAsync($"⏭️ {Context.User.Mention} skipped {what}.{next}",
            allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
    }

    [SlashCommand("stop", "Stops the music, clears the queue, and leaves the voice channel.")]
    public async Task StopAsync()
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        await player.StopAsync();
        await player.DisposeAsync(); // disconnects from voice and forgets the player
        await RespondAsync($"⏹️ {Context.User.Mention} stopped the music.", allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("nowplaying", "Shows the song that's playing.")]
    public async Task NowPlayingAsync()
    {
        if (await GetPlayerAsync(requireSameChannel: false) is not { } player)
        {
            return;
        }

        if (player.CurrentTrack is not { } track)
        {
            await RespondAsync("Nothing is playing.", ephemeral: true);
            return;
        }

        var embed = MusicFormatting.NowPlayingEmbed(track, player,
            player.State == PlayerState.Paused ? "Paused" : "Now playing").ToEmbedBuilder();
        if (!track.IsLiveStream && player.Position is { } position)
        {
            embed.Description += "\n" + MusicFormatting.ProgressBar(position.Position, track.Duration);
        }

        await RespondAsync(embed: embed.Build(), allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("queue", "Shows the upcoming songs.")]
    public async Task QueueAsync([Summary(description: "Page number")] [MinValue(1)] int page = 1)
    {
        if (await GetPlayerAsync(requireSameChannel: false) is not { } player)
        {
            return;
        }

        if (player.CurrentTrack is null && player.Queue.Count == 0)
        {
            await RespondAsync("The queue is empty - add something with `/play`.", ephemeral: true);
            return;
        }

        var pages = Math.Max(1, (int)Math.Ceiling(player.Queue.Count / (double)QueuePageSize));
        page = Math.Min(page, pages);

        var text = new StringBuilder();
        if (player.CurrentTrack is { } current)
        {
            text.AppendLine($"**Now playing:** {MusicFormatting.TrackLink(current)} ({MusicFormatting.Length(current)})").AppendLine();
        }

        if (player.Queue.Count == 0)
        {
            text.AppendLine("*Nothing queued after this.*");
        }

        var start = (page - 1) * QueuePageSize;
        for (var i = start; i < Math.Min(start + QueuePageSize, player.Queue.Count); i++)
        {
            var track = player.Queue[i].Track;
            text.AppendLine($"`{i + 1}.` {MusicFormatting.TrackLink(track)} ({(track is null ? "?" : MusicFormatting.Length(track))})");
        }

        var total = player.Queue.Select(item => item.Track).Where(t => t is { IsLiveStream: false })
            .Aggregate(TimeSpan.Zero, (sum, t) => sum + t!.Duration);

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x1DB954))
            .WithTitle("Queue")
            .WithDescription(text.ToString())
            .WithFooter($"{player.Queue.Count} song(s) queued · {MusicFormatting.Duration(total)} total · " +
                        $"Loop: {MusicFormatting.RepeatLabel(player.RepeatMode)} · Page {page}/{pages}")
            .Build();

        await RespondAsync(embed: embed, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("remove", "Removes a song from the queue.")]
    public async Task RemoveAsync([Summary(description: "Its position, as shown by /queue")] [MinValue(1)] int position)
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        if (position > player.Queue.Count)
        {
            await RespondAsync($"There's no song at position {position} - the queue has {player.Queue.Count}.", ephemeral: true);
            return;
        }

        var track = player.Queue[position - 1].Track;
        await player.Queue.RemoveAtAsync(position - 1);
        await RespondAsync($"🗑️ Removed {MusicFormatting.TrackLink(track)} from the queue.",
            allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
    }

    [SlashCommand("move", "Moves a song to a different spot in the queue.")]
    public async Task MoveAsync(
        [Summary(description: "The song's current position, as shown by /queue")] [MinValue(1)] int from,
        [Summary(description: "Where to move it (1 = play next)")] [MinValue(1)] int to)
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        if (from > player.Queue.Count)
        {
            await RespondAsync($"There's no song at position {from} - the queue has {player.Queue.Count}.", ephemeral: true);
            return;
        }

        to = Math.Min(to, player.Queue.Count);
        var item = player.Queue[from - 1];
        await player.Queue.RemoveAtAsync(from - 1);
        await player.Queue.InsertAsync(to - 1, item);
        await RespondAsync($"↕️ Moved {MusicFormatting.TrackLink(item.Track)} to position **{to}**.",
            allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
    }

    [SlashCommand("shuffle", "Shuffles the songs in the queue.")]
    public async Task ShuffleAsync()
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        if (player.Queue.Count < 2)
        {
            await RespondAsync("There need to be at least 2 songs queued to shuffle.", ephemeral: true);
            return;
        }

        await player.Queue.ShuffleAsync();
        await RespondAsync($"🔀 {Context.User.Mention} shuffled the queue ({player.Queue.Count} songs).",
            allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("loop", "Repeats the current song or the whole queue.")]
    public async Task LoopAsync([Summary(description: "What to repeat")] LoopMode mode)
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        player.RepeatMode = mode switch
        {
            LoopMode.Song => TrackRepeatMode.Track,
            LoopMode.Queue => TrackRepeatMode.Queue,
            _ => TrackRepeatMode.None,
        };

        await RespondAsync(mode switch
        {
            LoopMode.Song => $"🔂 {Context.User.Mention} turned on looping for the current song.",
            LoopMode.Queue => $"🔁 {Context.User.Mention} turned on looping for the whole queue.",
            _ => $"➡️ {Context.User.Mention} turned looping off.",
        }, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("clearqueue", "Removes every upcoming song (the current one keeps playing).")]
    public async Task ClearQueueAsync()
    {
        if (await GetControllablePlayerAsync() is not { } player)
        {
            return;
        }

        var removed = await player.Queue.ClearAsync();
        await RespondAsync(removed == 0
            ? "The queue was already empty."
            : $"🧹 {Context.User.Mention} cleared {removed} song(s) from the queue.", allowedMentions: AllowedMentions.None);
    }

    /// <summary>Why the bot can't join the caller's voice channel to play, or null if it can.</summary>
    private string? GetJoinProblem()
    {
        if ((Context.User as IGuildUser)?.VoiceChannel is not { } voiceChannel)
        {
            return "Join a voice channel first.";
        }

        if (_audioService.Players.TryGetPlayer(Context.Guild.Id, out var existing) && existing.VoiceChannelId != voiceChannel.Id)
        {
            return $"I'm already playing in <#{existing.VoiceChannelId}> - join that channel to add songs.";
        }

        var permissions = Context.Guild.CurrentUser.GetPermissions(voiceChannel);
        if (!permissions.ViewChannel || !permissions.Connect || !permissions.Speak)
        {
            return $"I need **View Channel**, **Connect**, and **Speak** in {voiceChannel.Mention} to play music there.";
        }

        // Move Members lets the bot join a full channel anyway; without it, joining would just time out.
        if (voiceChannel is SocketVoiceChannel { UserLimit: > 0 and var limit } socketChannel &&
            socketChannel.ConnectedUsers.Count >= limit && !permissions.MoveMembers)
        {
            return $"{voiceChannel.Mention} is full.";
        }

        return null;
    }

    /// <summary>Gets (or creates and connects) the player for the caller's voice channel. Runs after DeferAsync, so
    /// errors go out as followups.</summary>
    private async Task<MusicPlayer?> JoinPlayerAsync()
    {
        var voiceChannelId = (Context.User as IGuildUser)?.VoiceChannel?.Id;
        var playerOptions = new MusicPlayerOptions
        {
            TextChannel = Context.Channel as ITextChannel,
            SelfDeaf = true,
            ClearQueueOnStop = true,
        };

        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            var result = await _audioService.Players.RetrieveAsync(Context.Guild.Id, voiceChannelId, PlayerFactoryInstance,
                Options.Create(playerOptions),
                new PlayerRetrieveOptions(PlayerChannelBehavior.Join, MemberVoiceStateBehavior.RequireSame),
                timeout.Token);

            if (result.IsSuccess)
            {
                return result.Player;
            }

            await FollowupAsync(result.Status switch
            {
                PlayerRetrieveStatus.UserNotInVoiceChannel => "Join a voice channel first.",
                PlayerRetrieveStatus.VoiceChannelMismatch => "I'm playing in a different voice channel - join that one to add songs.",
                _ => "Couldn't join your voice channel.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't get a music player for guild {GuildId}. Is Lavalink running at {Url}?",
                Context.Guild.Id, _options.LavalinkUrl);
            await FollowupAsync("The music server isn't reachable right now - let a server admin know.");
        }

        return null;
    }

    /// <summary>The player for commands that change playback: the caller must be in the bot's voice channel.</summary>
    private Task<MusicPlayer?> GetControllablePlayerAsync() => GetPlayerAsync(requireSameChannel: true);

    private async Task<MusicPlayer?> GetPlayerAsync(bool requireSameChannel)
    {
        if (!_audioService.Players.TryGetPlayer<MusicPlayer>(Context.Guild.Id, out var player) ||
            player is null || player.State == PlayerState.Destroyed)
        {
            await RespondAsync("I'm not playing anything right now - start something with `/play`.", ephemeral: true);
            return null;
        }

        var user = Context.User as IGuildUser;
        if (requireSameChannel && user?.VoiceChannel?.Id != player.VoiceChannelId && !IsModerator(user))
        {
            await RespondAsync($"You need to be in <#{player.VoiceChannelId}> to control the music.", ephemeral: true);
            return null;
        }

        return player;
    }

    /// <summary>Moderators can control the player from anywhere, e.g. to stop an offensive song without joining the
    /// voice channel. Move Members/Manage Messages match the permissions they already use for voice/text moderation.</summary>
    private static bool IsModerator(IGuildUser? user)
        => user is not null && (user.GuildPermissions.MoveMembers || user.GuildPermissions.ManageMessages);

    private static bool IsAllowedLinkHost(string host) => AllowedLinkHosts.Any(allowed => HostMatches(host, allowed));

    /// <summary>True for <paramref name="domain"/> itself or any subdomain of it - but not e.g. "notyoutube.com".</summary>
    private static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
