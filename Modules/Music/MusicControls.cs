using System.Text;
using Discord;
using Discord.WebSocket;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using ModBot.Common;
using ModBot.Services.Moderation;

namespace ModBot.Modules.Music;

public enum LoopMode
{
    Off,
    Song,
    Queue,
}

/// <summary>
/// Every music control as the person asking, shared by the slash commands, the menu's buttons and the chatbot so all
/// three follow the same rules. Anyone can add songs from anywhere; controlling what's playing (pause, stop, loop,
/// voting to skip) means being in the bot's voice channel, except for moderators.
/// </summary>
public class MusicControls
{
    private const int QueuePageSize = 10;

    private readonly MusicService _music;
    private readonly MusicQueueService _queue;

    public MusicControls(MusicService music, MusicQueueService queue)
    {
        _music = music;
        _queue = queue;
    }

    /// <summary>Adds a song (or playlist) to the queue, and starts the music if nothing's playing and the person is
    /// in a voice channel.</summary>
    public Task<MusicReply> PlayAsync(SocketGuildUser user, ITextChannel channel, string query)
        => _queue.AddAsync(user, query, channel, startIfIdle: true);

    /// <summary>Adds a song to the queue without starting anything: for building it up first.</summary>
    public Task<MusicReply> AddAsync(SocketGuildUser user, ITextChannel channel, string query)
        => _queue.AddAsync(user, query, channel, startIfIdle: false);

    public Task<MusicReply> StartAsync(SocketGuildUser user, ITextChannel channel) => _queue.StartAsync(user, channel);

    public Task<MusicReply> SkipAsync(SocketGuildUser user) => _queue.VoteSkipAsync(user);

    public Task<MusicReply> RemoveAsync(SocketGuildUser user, int position) => _queue.RemoveAsync(user, position);

    public Task<MusicReply> RemoveMineAsync(SocketGuildUser user) => _queue.RemoveMineAsync(user);

    public Task<MusicReply> ClearQueueAsync(SocketGuildUser user) => _queue.ClearAsync(user);

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
        _queue.RefreshMenu(user.Guild.Id);
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
        _queue.RefreshMenu(user.Guild.Id);
        return MusicReply.Ok($"▶️ Resumed by {user.Mention}.", suppressEmbeds: false);
    }

    /// <summary>Stops the music and leaves voice. The queue stays, so the next Play picks up from there.</summary>
    public async Task<MusicReply> StopAsync(SocketGuildUser user)
    {
        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        await player.StopAsync();
        await player.DisposeAsync(); // disconnects from voice and forgets the player
        var waiting = (await _queue.GetAsync(user.Guild.Id)).Songs.Count;
        return MusicReply.Ok($"⏹️ {user.Mention} stopped the music." + (waiting == 0
            ? ""
            : $" The queue's kept ({waiting} song{(waiting == 1 ? "" : "s")} waiting) - press **Play** to carry on."),
            suppressEmbeds: false);
    }

    public MusicReply NowPlaying(SocketGuildUser user)
    {
        if (_music.GetPlayer(user.Guild.Id) is not { CurrentItem: { Track: { } track } item } player)
        {
            return MusicReply.Fail("Nothing's playing right now. See what's waiting with `/queue`.");
        }

        var title = player.State == PlayerState.Paused ? "Paused" : "Now playing";
        if (item is QueueTrackItem queued)
        {
            title += $" - {Format.Sanitize(queued.Song.PickerName)}'s pick";
        }

        var embed = MusicFormatting.NowPlayingEmbed(track, player, title).ToEmbedBuilder();
        if (!track.IsLiveStream && player.Position is { } position)
        {
            embed.Description += "\n" + MusicFormatting.ProgressBar(position.Position, track.Duration);
        }

        return new(true, null, embed.Build());
    }

    /// <summary>The queue in play order (whose turn is next), with what's playing.</summary>
    public async Task<MusicReply> QueueAsync(SocketGuildUser user, int page = 1)
    {
        var state = await _queue.GetAsync(user.Guild.Id);
        var order = MusicQueueRules.Order(state);
        var player = _music.GetPlayer(user.Guild.Id);
        if (player?.CurrentItem is null && order.Count == 0)
        {
            return MusicReply.Fail("The queue is empty - add something with `/add` or `/play`.");
        }

        var pages = Math.Max(1, (int)Math.Ceiling(order.Count / (double)QueuePageSize));
        page = Math.Clamp(page, 1, pages);

        var text = new StringBuilder();
        if (player?.CurrentItem is { } current)
        {
            var picker = current is QueueTrackItem queued ? $" - <@{queued.Song.PickerId}>" : "";
            text.AppendLine($"**Now playing:** {MusicFormatting.TrackLink(current.Track)}{picker}").AppendLine();
        }
        else
        {
            text.AppendLine("*Not playing - join a voice channel and press **Play** on the music menu (or `/start`).*")
                .AppendLine();
        }

        if (order.Count == 0)
        {
            text.AppendLine("*Nothing waiting after this.*");
        }

        var start = (page - 1) * QueuePageSize;
        for (var i = start; i < Math.Min(start + QueuePageSize, order.Count); i++)
        {
            var song = order[i];
            text.AppendLine($"`{i + 1}.` {MusicFormatting.SongLink(song)} ({MusicFormatting.SongLength(song)}) - " +
                            $"<@{song.PickerId}>");
        }

        var total = order.Where(s => !s.IsLiveStream).Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
        var loop = player?.RepeatMode == TrackRepeatMode.Track ? "Current song" : state.LoopQueue ? "Whole queue" : "Off";
        var embed = new EmbedBuilder()
            .WithColor(Colors.Music)
            .WithTitle("Queue - in turn order")
            .WithDescription(text.ToString())
            .WithFooter($"{order.Count} song(s) waiting · {MusicFormatting.Duration(total)} total · Loop: {loop} · " +
                        $"Page {page}/{pages}")
            .Build();

        return new(true, null, embed);
    }

    public async Task<MusicReply> LoopAsync(SocketGuildUser user, LoopMode mode)
    {
        // Changing what's playing is for the people listening. With nothing playing, looping the queue (or turning
        // it off) is just a setting for later, so anyone may.
        var (player, error) = GetControllable(user);
        if (player is null && (mode == LoopMode.Song || _music.GetPlayer(user.Guild.Id) is not null))
        {
            return MusicReply.Fail(error);
        }

        if (player is not null)
        {
            player.RepeatMode = mode == LoopMode.Song ? TrackRepeatMode.Track : TrackRepeatMode.None;
        }

        await _queue.SetLoopQueueAsync(user.Guild.Id, mode == LoopMode.Queue);
        return MusicReply.Ok(mode switch
        {
            LoopMode.Song => $"🔂 {user.Mention} turned on looping for the current song (a skip still moves on).",
            LoopMode.Queue => $"🔁 {user.Mention} turned on looping for the queue: finished songs go back in.",
            _ => $"➡️ {user.Mention} turned looping off.",
        }, suppressEmbeds: false);
    }

    /// <summary>The player, if <paramref name="user"/> may control it: they must be in its voice channel, unless
    /// they're a moderator.</summary>
    private (MusicPlayer? Player, string Error) GetControllable(SocketGuildUser user)
    {
        if (_music.GetPlayer(user.Guild.Id) is not { } player)
        {
            return (null, "I'm not playing anything right now. Join a voice channel and press **Play** on the music " +
                          "menu (or use `/start`).");
        }

        if (user.VoiceChannel?.Id != player.VoiceChannelId && !Moderators.IsModerator(user))
        {
            return (null, $"You need to be in <#{player.VoiceChannelId}> to control the music.");
        }

        return (player, string.Empty);
    }
}
