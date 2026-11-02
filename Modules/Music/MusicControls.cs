using System.Text;
using Discord;
using Discord.WebSocket;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Tracks;

namespace ModBot.Modules.Music;

public enum LoopMode
{
    Off,
    Song,
    Queue,
}

/// <summary>
/// Every music control (play, pause, skip, the queue...) as the person asking, shared by the slash commands and the
/// chatbot so both follow the same rules: controls only from the bot's voice channel (moderators from anywhere), and
/// in a music session, skips are votes and the turn order is the session's to keep.
/// </summary>
public class MusicControls
{
    private const int QueuePageSize = 10;

    private readonly MusicService _music;
    private readonly MusicSessionService _sessions;

    public MusicControls(MusicService music, MusicSessionService sessions)
    {
        _music = music;
        _sessions = sessions;
    }

    /// <summary>Plays or queues a song. During a music session it goes into the session's turns instead.</summary>
    public async Task<MusicReply> PlayAsync(SocketGuildUser user, ITextChannel announcementChannel, string query,
        bool replyShowsNowPlaying)
    {
        if (_sessions.Get(user.Guild.Id) is { } session)
        {
            return await _sessions.AddAsync(session, user, query);
        }

        var result = await _music.PlayAsync(user, announcementChannel, query, replyShowsNowPlaying);
        return new(result.Success, result.Text, result.Embed, result.SuppressEmbeds);
    }

    public async Task<MusicReply> PauseAsync(SocketGuildUser user)
    {
        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        if (player.State == PlayerState.Paused)
        {
            return MusicReply.Fail("Already paused - use `/resume` to continue.");
        }

        await player.PauseAsync();
        return MusicReply.Ok($"⏸️ Paused by {user.Mention}.", suppressEmbeds: false);
    }

    public async Task<MusicReply> ResumeAsync(SocketGuildUser user)
    {
        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        if (player.State != PlayerState.Paused)
        {
            return MusicReply.Fail("Nothing is paused.");
        }

        await player.ResumeAsync();
        return MusicReply.Ok($"▶️ Resumed by {user.Mention}.", suppressEmbeds: false);
    }

    /// <summary>Skips <paramref name="count"/> songs - or, in a music session, votes to skip the current one.</summary>
    public async Task<MusicReply> SkipAsync(SocketGuildUser user, int count = 1)
    {
        if (StartedSession(user.Guild.Id) is { } session)
        {
            return count > 1
                ? MusicReply.Fail("In a music session, songs are skipped one at a time by vote. Vote to skip this one?")
                : await _sessions.VoteSkipAsync(session, user);
        }

        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        // Under the queue lock, so a /play or /remove at the same moment can't change the queue between counting it
        // and skipping.
        LavalinkTrack? skipped;
        var queueLock = await _music.AcquireQueueLockAsync(user.Guild.Id);
        try
        {
            if (player.CurrentItem is null)
            {
                return MusicReply.Fail("Nothing's playing to skip.");
            }

            skipped = player.CurrentTrack;
            count = Math.Min(Math.Max(1, count), player.Queue.Count + 1);
            await player.SkipAsync(count);
        }
        finally
        {
            queueLock.Release();
        }

        var what = count == 1 ? MusicFormatting.TrackLink(skipped) : $"**{count}** songs";
        var next = player.CurrentTrack is { } current ? $" Now playing {MusicFormatting.TrackLink(current)}." : " The queue is empty.";
        return MusicReply.Ok($"⏭️ {user.Mention} skipped {what}.{next}");
    }

    public async Task<MusicReply> StopAsync(SocketGuildUser user)
    {
        if (StartedSession(user.Guild.Id) is { } session)
        {
            // Stopping ends the session for everyone, so it's the host's call, like ending it.
            return session.HostId == user.Id || MusicPermissions.IsModerator(user)
                ? await _sessions.EndAsync(session, user)
                : MusicReply.Fail($"In a music session only the host (<@{session.HostId}>) or a moderator can stop " +
                                  "the music. Vote to skip instead, or leave the session.");
        }

        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        await player.StopAsync();
        await player.DisposeAsync(); // disconnects from voice and forgets the player
        return MusicReply.Ok($"⏹️ {user.Mention} stopped the music.", suppressEmbeds: false);
    }

    public MusicReply NowPlaying(SocketGuildUser user)
    {
        if (_music.GetPlayer(user.Guild.Id) is not { } player)
        {
            return MusicReply.Fail(NotPlayingText(user.Guild.Id));
        }

        if (player.CurrentItem is not { Track: { } track } item)
        {
            return MusicReply.Fail("Nothing is playing.");
        }

        var title = player.State == PlayerState.Paused ? "Paused" : "Now playing";
        if (item is SessionTrackItem pick)
        {
            title += $" - {pick.PickerName}'s pick";
        }

        var embed = MusicFormatting.NowPlayingEmbed(track, player, title).ToEmbedBuilder();
        if (!track.IsLiveStream && player.Position is { } position)
        {
            embed.Description += "\n" + MusicFormatting.ProgressBar(position.Position, track.Duration);
        }

        return new(true, null, embed.Build());
    }

    public MusicReply Queue(SocketGuildUser user, int page = 1)
    {
        if (_music.GetPlayer(user.Guild.Id) is not { } player)
        {
            return MusicReply.Fail(NotPlayingText(user.Guild.Id));
        }

        if (player.CurrentItem is null && player.Queue.Count == 0)
        {
            return MusicReply.Fail("The queue is empty - add something with `/play`.");
        }

        var pages = Math.Max(1, (int)Math.Ceiling(player.Queue.Count / (double)QueuePageSize));
        page = Math.Clamp(page, 1, pages);

        var text = new StringBuilder();
        if (player.CurrentItem is { } current)
        {
            text.AppendLine($"**Now playing:** {MusicFormatting.TrackLink(current.Track)} " +
                            $"({(current.Track is null ? "?" : MusicFormatting.Length(current.Track))}){PickedBy(current)}")
                .AppendLine();
        }

        if (player.Queue.Count == 0)
        {
            text.AppendLine("*Nothing queued after this.*");
        }

        var start = (page - 1) * QueuePageSize;
        for (var i = start; i < Math.Min(start + QueuePageSize, player.Queue.Count); i++)
        {
            var item = player.Queue[i];
            var track = item.Track;
            text.AppendLine($"`{i + 1}.` {MusicFormatting.TrackLink(track)} " +
                            $"({(track is null ? "?" : MusicFormatting.Length(track))}){PickedBy(item)}");
        }

        var total = player.Queue.Select(item => item.Track).Where(t => t is { IsLiveStream: false })
            .Aggregate(TimeSpan.Zero, (sum, t) => sum + t!.Duration);

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x1DB954))
            .WithTitle(StartedSession(user.Guild.Id) is null ? "Queue" : "Queue - music session (in turn order)")
            .WithDescription(text.ToString())
            .WithFooter($"{player.Queue.Count} song(s) queued · {MusicFormatting.Duration(total)} total · " +
                        $"Loop: {MusicFormatting.RepeatLabel(player.RepeatMode)} · Page {page}/{pages}")
            .Build();

        return new(true, null, embed);
    }

    public async Task<MusicReply> RemoveAsync(SocketGuildUser user, int position)
    {
        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        var session = StartedSession(user.Guild.Id);
        LavalinkTrack? track;
        var queueLock = await _music.AcquireQueueLockAsync(user.Guild.Id);
        try
        {
            if (position < 1 || position > player.Queue.Count)
            {
                return MusicReply.Fail($"There's no song at position {position} - the queue has {player.Queue.Count}.");
            }

            var item = player.Queue[position - 1];
            if (session is not null && MusicSessionService.PickerOf(item) != user.Id && session.HostId != user.Id &&
                !MusicPermissions.IsModerator(user))
            {
                return MusicReply.Fail("In a music session you can only remove your own songs - vote to skip others " +
                                       "when they come on.");
            }

            track = item.Track;
            await player.Queue.RemoveAtAsync(position - 1);
        }
        finally
        {
            queueLock.Release();
        }

        RefreshSessionMenu(session);
        return MusicReply.Ok($"🗑️ Removed {MusicFormatting.TrackLink(track)} from the queue.");
    }

    public async Task<MusicReply> MoveAsync(SocketGuildUser user, int from, int to)
    {
        if (StartedSession(user.Guild.Id) is not null)
        {
            return MusicReply.Fail("A music session keeps the songs in turn order, so they can't be moved around.");
        }

        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        ITrackQueueItem item;
        var queueLock = await _music.AcquireQueueLockAsync(user.Guild.Id);
        try
        {
            if (from < 1 || from > player.Queue.Count)
            {
                return MusicReply.Fail($"There's no song at position {from} - the queue has {player.Queue.Count}.");
            }

            to = Math.Clamp(to, 1, player.Queue.Count);
            item = player.Queue[from - 1];
            await player.Queue.RemoveAtAsync(from - 1);
            await player.Queue.InsertAsync(to - 1, item);
        }
        finally
        {
            queueLock.Release();
        }

        return MusicReply.Ok($"↕️ Moved {MusicFormatting.TrackLink(item.Track)} to position **{to}**.");
    }

    public async Task<MusicReply> ShuffleAsync(SocketGuildUser user)
    {
        if (StartedSession(user.Guild.Id) is not null)
        {
            return MusicReply.Fail("A music session keeps the songs in turn order, so there's nothing to shuffle.");
        }

        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        var queueLock = await _music.AcquireQueueLockAsync(user.Guild.Id);
        try
        {
            if (player.Queue.Count < 2)
            {
                return MusicReply.Fail("There need to be at least 2 songs queued to shuffle.");
            }

            await player.Queue.ShuffleAsync();
        }
        finally
        {
            queueLock.Release();
        }

        return MusicReply.Ok($"🔀 {user.Mention} shuffled the queue ({player.Queue.Count} songs).", suppressEmbeds: false);
    }

    public MusicReply Loop(SocketGuildUser user, LoopMode mode)
    {
        if (StartedSession(user.Guild.Id) is { } session && session.HostId != user.Id &&
            !MusicPermissions.IsModerator(user))
        {
            return MusicReply.Fail($"In a music session only the host (<@{session.HostId}>) can change looping, since " +
                                   "a looped song holds up everyone's turn.");
        }

        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        player.RepeatMode = mode switch
        {
            LoopMode.Song => TrackRepeatMode.Track,
            LoopMode.Queue => TrackRepeatMode.Queue,
            _ => TrackRepeatMode.None,
        };

        return MusicReply.Ok(mode switch
        {
            LoopMode.Song => $"🔂 {user.Mention} turned on looping for the current song.",
            LoopMode.Queue => $"🔁 {user.Mention} turned on looping for the whole queue.",
            _ => $"➡️ {user.Mention} turned looping off.",
        }, suppressEmbeds: false);
    }

    public async Task<MusicReply> ClearQueueAsync(SocketGuildUser user)
    {
        var session = StartedSession(user.Guild.Id);
        if (session is not null && session.HostId != user.Id && !MusicPermissions.IsModerator(user))
        {
            return MusicReply.Fail($"In a music session only the host (<@{session.HostId}>) or a moderator can clear " +
                                   "everyone's songs. You can leave the session to take out yours.");
        }

        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        int removed;
        var queueLock = await _music.AcquireQueueLockAsync(user.Guild.Id);
        try
        {
            removed = await player.Queue.ClearAsync();
        }
        finally
        {
            queueLock.Release();
        }

        RefreshSessionMenu(session);
        return removed == 0
            ? MusicReply.Fail("The queue was already empty.")
            : MusicReply.Ok($"🧹 {user.Mention} cleared {removed} song(s) from the queue.", suppressEmbeds: false);
    }

    /// <summary>The player, if <paramref name="user"/> may control it: they must be in its voice channel, unless
    /// they're a moderator.</summary>
    private (MusicPlayer? Player, string Error) GetControllable(SocketGuildUser user)
    {
        if (_music.GetPlayer(user.Guild.Id) is not { } player)
        {
            return (null, NotPlayingText(user.Guild.Id));
        }

        if (user.VoiceChannel?.Id != player.VoiceChannelId && !MusicPermissions.IsModerator(user))
        {
            return (null, $"You need to be in <#{player.VoiceChannelId}> to control the music.");
        }

        return (player, string.Empty);
    }

    private string NotPlayingText(ulong guildId)
        => _sessions.Get(guildId) is { Started: false }
            ? "The music session hasn't started yet - the host presses **Start** on the session menu when everyone's ready."
            : "I'm not playing anything right now - start something with `/play`.";

    private MusicSession? StartedSession(ulong guildId) => _sessions.Get(guildId) is { Started: true } session ? session : null;

    private void RefreshSessionMenu(MusicSession? session)
    {
        if (session is not null)
        {
            _sessions.RefreshMenu(session);
        }
    }

    private static string PickedBy(ITrackQueueItem item) => item is SessionTrackItem pick ? $" - <@{pick.PickerId}>" : "";
}
