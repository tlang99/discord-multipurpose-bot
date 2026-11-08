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
        => _queue.AddAsync(user, query, channel);

    public Task<MusicReply> StartAsync(SocketGuildUser user, ITextChannel channel) => _queue.StartAsync(user, channel);

    public Task<MusicReply> SkipAsync(SocketGuildUser user) => _queue.VoteSkipAsync(user);

    public Task<MusicReply> RemoveAsync(SocketGuildUser user, int position) => _queue.RemoveAsync(user, position);

    public Task<MusicReply> RemoveTitledAsync(SocketGuildUser user, string title) => _queue.RemoveTitledAsync(user, title);

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
            // Neutral, since it may have come from the menu's button, a command or the chatbot.
            return MusicReply.Fail("Already paused.");
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

        // With 3 or more in the channel, it ends the music for all of them, so it takes a vote (see VoteStop).
        if (_queue.VoteStop(user, player) is { } vote)
        {
            _queue.RefreshMenu(user.Guild.Id);
            return vote;
        }

        await player.StopAsync();
        await player.DisposeAsync(); // disconnects from voice and forgets the player
        var state = await _queue.GetAsync(user.Guild.Id);
        var waiting = state.Songs.Count;
        return MusicReply.Ok($"⏹️ {user.Mention} stopped the music." + (waiting == 0
            ? ""
            : $" The queue's kept ({waiting} song{(waiting == 1 ? "" : "s")} waiting) - press **Play** to carry on.") +
            // It's saved, so it'd quietly carry on next time.
            (state.LoopQueue ? " Queue looping is still on (the menu's Loop button turns it off)." : ""),
            suppressEmbeds: false);
    }

    /// <summary>The menu's Loop button: off, then the current song (while one's playing), then the whole queue, then
    /// off again.</summary>
    public async Task<MusicReply> LoopNextAsync(SocketGuildUser user)
    {
        var player = _music.GetPlayer(user.Guild.Id);
        var next = MusicQueueService.LoopOf(await _queue.GetAsync(user.Guild.Id), player) switch
        {
            LoopMode.Off when player?.CurrentItem is not null => LoopMode.Song,
            LoopMode.Off or LoopMode.Song => LoopMode.Queue,
            _ => LoopMode.Off,
        };
        return await LoopAsync(user, next); // which redraws the menu, button and all
    }

    /// <summary>How much the menu's 🔉 🔊 buttons and "turn it down" / "up" change the volume by.</summary>
    public const int VolumeStep = 10;

    /// <summary>Sets the volume to <paramref name="to"/> percent, or changes it by <paramref name="by"/>. Kept for the
    /// server between sessions. While the music plays, it's for the people listening (like pause); with nothing
    /// playing, it's just a setting for next time, so anyone may.</summary>
    public async Task<MusicReply> VolumeAsync(SocketGuildUser user, int? to = null, int by = 0)
    {
        MusicPlayer? player = null;
        if (_music.GetPlayer(user.Guild.Id) is not null)
        {
            (player, var error) = GetControllable(user);
            if (player is null)
            {
                return MusicReply.Fail(error);
            }
        }

        // Asked for a number past the limits ("volume 500"), it says so rather than quietly picking another, as the
        // slash command does. (Steps of 🔉 🔊 just stop at the ends.)
        if (to is < 0 or > MusicQueueRules.MaxVolume)
        {
            return MusicReply.Fail($"The volume goes from 0 to {MusicQueueRules.MaxVolume}% (100 is the song as it " +
                                   "is).");
        }

        var (was, now) = await _queue.SetVolumeAsync(user.Guild.Id, saved => to ?? saved + by, player);
        if (now == was)
        {
            return MusicReply.Fail(by > 0 && now == MusicQueueRules.MaxVolume
                ? $"It's already as loud as it goes ({now}%)."
                : by < 0 && now == 0
                    ? "It's already silent (0%)."
                    : $"The volume's already {now}%.");
        }

        return MusicReply.Ok($"{(now < was ? "🔉" : "🔊")} {user.Mention} set the volume to **{now}%**.",
            suppressEmbeds: false) with { Announce = false };
    }

    /// <summary>Jumps to <paramref name="to"/> in the song that's playing (zero restarts it).</summary>
    public async Task<MusicReply> SeekAsync(SocketGuildUser user, TimeSpan to)
    {
        var (player, error) = GetControllable(user);
        if (player is null)
        {
            return MusicReply.Fail(error);
        }

        if (player.CurrentItem?.Track is not { } track)
        {
            return MusicReply.Fail("Nothing's playing right now.");
        }

        if (track.IsLiveStream || !track.IsSeekable)
        {
            return MusicReply.Fail("This one's a live stream (or can't be jumped around in), so there's nowhere to " +
                                   "go.");
        }

        if (to >= track.Duration)
        {
            return MusicReply.Fail($"That's past the end - {MusicFormatting.TrackLink(track)} is " +
                                   $"{MusicFormatting.Length(track)} long.");
        }

        await player.SeekAsync(to);
        return MusicReply.Ok(to == TimeSpan.Zero
            ? $"⏪ {user.Mention} restarted {MusicFormatting.TrackLink(track)}."
            : $"⏩ {user.Mention} jumped to {MusicFormatting.Duration(to)} in {MusicFormatting.TrackLink(track)}.",
            suppressEmbeds: false);
    }

    /// <summary>Goes back to the song before this one (see <see cref="MusicQueueService.BackAsync"/>). With nothing
    /// playing, "replay the last song" means the last one played: it's added again (and starts, if they're in
    /// voice), as "play that again" does.</summary>
    public async Task<MusicReply> BackAsync(SocketGuildUser user, ITextChannel channel)
    {
        if (_music.GetPlayer(user.Guild.Id)?.CurrentItem is null)
        {
            return await AgainAsync(user, channel);
        }

        var (player, error) = GetControllable(user);
        return player is null ? MusicReply.Fail(error) : await _queue.BackAsync(user, player);
    }

    /// <summary>"Play that again": the newest song in the history, or the one picked from the History list.</summary>
    public Task<MusicReply> AgainAsync(SocketGuildUser user, ITextChannel channel, Guid? songId = null)
        => _queue.AgainAsync(user, channel, songId);

    /// <summary>The recently played songs, newest first, as a list to read.</summary>
    public async Task<MusicReply> HistoryAsync(SocketGuildUser user)
    {
        var history = await _queue.HistoryAsync(user.Guild.Id);
        if (history.Count == 0)
        {
            return MusicReply.Fail("Nothing's been played yet.");
        }

        var lines = history.Take(10).Select((s, i) => $"`{i + 1}.` {MusicFormatting.SongLink(s)} - <@{s.PickerId}>");
        return new(true, null, new EmbedBuilder()
            .WithColor(Colors.Music)
            .WithTitle("Recently played")
            .WithDescription(string.Join('\n', lines))
            .WithFooter("Newest first · \"play that again\" adds the newest, or pick one from the menu's History")
            .Build());
    }

    public Task<MusicReply> PlayNextAsync(SocketGuildUser user, int position) => _queue.PlayNextAsync(user, position);

    public Task<MusicReply> ShuffleMineAsync(SocketGuildUser user) => _queue.ShuffleMineAsync(user);

    public MusicReply NowPlaying(SocketGuildUser user)
    {
        if (_music.GetPlayer(user.Guild.Id) is not { CurrentItem: { Track: { } track } item } player)
        {
            return MusicReply.Fail("Nothing's playing right now. See what's waiting with `/queue`.");
        }

        var title = player.State == PlayerState.Paused ? "Paused" : "Now playing";
        if (item is QueueTrackItem queued)
        {
            title += $" - {TextTools.Escape(queued.Song.PickerName)}'s pick";
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
        var loop = MusicQueueService.LoopOf(state, player) switch
        {
            LoopMode.Song => "Current song",
            LoopMode.Queue => "Whole queue",
            _ => "Off",
        };
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
