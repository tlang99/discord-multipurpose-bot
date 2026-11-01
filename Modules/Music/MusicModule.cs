using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Lavalink4NET;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Tracks;

namespace ModBot.Modules.Music;

/// <summary>
/// Music playback through Lavalink. Anyone can use these, but only from the voice channel the bot is playing in, so
/// people outside the channel can't control what everyone in it is hearing. Moderators (Move Members or Manage
/// Messages) can control it from anywhere. All of them only work in the music channel (see
/// <see cref="RequireMusicChannelAttribute"/>), so they don't flood the main chat.
/// </summary>
[RequireContext(ContextType.Guild)]
[RequireMusicChannel]
public class MusicModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int QueuePageSize = 10;

    private readonly IAudioService _audioService;
    private readonly MusicService _music;

    public MusicModule(IAudioService audioService, MusicService music)
    {
        _audioService = audioService;
        _music = music;
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
        if (query.Trim().Length == 0)
        {
            await RespondAsync("Tell me what to play.", ephemeral: true);
            return;
        }

        // Acknowledge first: Discord only allows 3 seconds, and joining voice + searching takes longer than that. (It
        // means problems are shown publicly rather than only to the caller.)
        await DeferAsync();

        if (Context.User is not SocketGuildUser user || Context.Channel is not ITextChannel channel)
        {
            await FollowupAsync("Music only works in a server text channel.");
            return;
        }

        // This reply is the "Now playing" message, so the player doesn't announce the first song again.
        var result = await _music.PlayAsync(user, channel, query, replyShowsNowPlaying: true);
        await FollowupAsync(text: result.Text, embed: result.Embed, allowedMentions: AllowedMentions.None,
            flags: result.SuppressEmbeds ? MessageFlags.SuppressEmbeds : MessageFlags.None);
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

        // Under the queue lock, so a /play or /remove at the same moment can't change the queue between counting it
        // and skipping.
        LavalinkTrack? skipped;
        var queueLock = await AcquireQueueLockAsync();
        try
        {
            skipped = player.CurrentTrack;
            count = Math.Min(count, player.Queue.Count + 1);
            await player.SkipAsync(count);
        }
        finally
        {
            queueLock.Release();
        }


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

        LavalinkTrack? track;
        var queueLock = await AcquireQueueLockAsync();
        try
        {
            if (position > player.Queue.Count)
            {
                await RespondAsync($"There's no song at position {position} - the queue has {player.Queue.Count}.", ephemeral: true);
                return;
            }

            track = player.Queue[position - 1].Track;
            await player.Queue.RemoveAtAsync(position - 1);
        }
        finally
        {
            queueLock.Release();
        }

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

        ITrackQueueItem item;
        var queueLock = await AcquireQueueLockAsync();
        try
        {
            if (from > player.Queue.Count)
            {
                await RespondAsync($"There's no song at position {from} - the queue has {player.Queue.Count}.", ephemeral: true);
                return;
            }

            to = Math.Min(to, player.Queue.Count);
            item = player.Queue[from - 1];
            await player.Queue.RemoveAtAsync(from - 1);
            await player.Queue.InsertAsync(to - 1, item);
        }
        finally
        {
            queueLock.Release();
        }

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

        var queueLock = await AcquireQueueLockAsync();
        try
        {
            if (player.Queue.Count < 2)
            {
                await RespondAsync("There need to be at least 2 songs queued to shuffle.", ephemeral: true);
                return;
            }

            await player.Queue.ShuffleAsync();
        }
        finally
        {
            queueLock.Release();
        }

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

        int removed;
        var queueLock = await AcquireQueueLockAsync();
        try
        {
            removed = await player.Queue.ClearAsync();
        }
        finally
        {
            queueLock.Release();
        }

        await RespondAsync(removed == 0
            ? "The queue was already empty."
            : $"🧹 {Context.User.Mention} cleared {removed} song(s) from the queue.", allowedMentions: AllowedMentions.None);
    }

    private Task<SemaphoreSlim> AcquireQueueLockAsync() => _music.AcquireQueueLockAsync(Context.Guild.Id);

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
}
