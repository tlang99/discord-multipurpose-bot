using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace ModBot.Modules.Music;

/// <summary>
/// Music playback through Lavalink. Anyone can use these, but only from the voice channel the bot is playing in, so
/// people outside the channel can't control what everyone in it is hearing. Moderators (Move Members or Manage
/// Messages) can control it from anywhere. All of them only work in the music channel (see
/// <see cref="RequireMusicChannelAttribute"/>), so they don't flood the main chat. The rules live in
/// <see cref="MusicControls"/>, shared with the chatbot.
/// </summary>
[RequireContext(ContextType.Guild)]
[RequireMusicChannel]
public class MusicModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly MusicControls _controls;

    public MusicModule(MusicControls controls)
    {
        _controls = controls;
    }

    public enum LoopChoice
    {
        [ChoiceDisplay("Off")] Off,
        [ChoiceDisplay("Current song")] Song,
        [ChoiceDisplay("Whole queue")] Queue,
    }

    [SlashCommand("play", "Plays a song or playlist, or adds it to the queue (or your turns, in a music session).")]
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
        var result = await _controls.PlayAsync(user, channel, query, replyShowsNowPlaying: true);
        await FollowupAsync(text: result.Text, embed: result.Embed, allowedMentions: AllowedMentions.None,
            flags: result.SuppressEmbeds ? MessageFlags.SuppressEmbeds : MessageFlags.None);
    }

    [SlashCommand("pause", "Pauses the current song.")]
    public Task PauseAsync() => RunAsync(user => _controls.PauseAsync(user));

    [SlashCommand("resume", "Resumes a paused song.")]
    public Task ResumeAsync() => RunAsync(user => _controls.ResumeAsync(user));

    [SlashCommand("skip", "Skips the current song (or several). In a music session, votes to skip it.")]
    public Task SkipAsync(
        [Summary(description: "How many songs to skip, including the current one")] [MinValue(1)] int count = 1)
        => RunAsync(user => _controls.SkipAsync(user, count));

    [SlashCommand("stop", "Stops the music, clears the queue, and leaves the voice channel.")]
    public Task StopAsync() => RunAsync(user => _controls.StopAsync(user));

    [SlashCommand("nowplaying", "Shows the song that's playing.")]
    public Task NowPlayingAsync() => RunAsync(user => Task.FromResult(_controls.NowPlaying(user)));

    [SlashCommand("queue", "Shows the upcoming songs.")]
    public Task QueueAsync([Summary(description: "Page number")] [MinValue(1)] int page = 1)
        => RunAsync(user => Task.FromResult(_controls.Queue(user, page)));

    [SlashCommand("remove", "Removes a song from the queue.")]
    public Task RemoveAsync([Summary(description: "Its position, as shown by /queue")] [MinValue(1)] int position)
        => RunAsync(user => _controls.RemoveAsync(user, position));

    [SlashCommand("move", "Moves a song to a different spot in the queue.")]
    public Task MoveAsync(
        [Summary(description: "The song's current position, as shown by /queue")] [MinValue(1)] int from,
        [Summary(description: "Where to move it (1 = play next)")] [MinValue(1)] int to)
        => RunAsync(user => _controls.MoveAsync(user, from, to));

    [SlashCommand("shuffle", "Shuffles the songs in the queue.")]
    public Task ShuffleAsync() => RunAsync(user => _controls.ShuffleAsync(user));

    [SlashCommand("loop", "Repeats the current song or the whole queue.")]
    public Task LoopAsync([Summary(description: "What to repeat")] LoopChoice mode)
        => RunAsync(user => Task.FromResult(_controls.Loop(user, mode switch
        {
            LoopChoice.Song => LoopMode.Song,
            LoopChoice.Queue => LoopMode.Queue,
            _ => LoopMode.Off,
        })));

    [SlashCommand("clearqueue", "Removes every upcoming song (the current one keeps playing).")]
    public Task ClearQueueAsync() => RunAsync(user => _controls.ClearQueueAsync(user));

    /// <summary>Runs a control and replies: publicly when it worked (everyone listening should know), privately when
    /// it didn't.</summary>
    private async Task RunAsync(Func<SocketGuildUser, Task<MusicReply>> control)
    {
        if (Context.User is not SocketGuildUser user)
        {
            await RespondAsync("Music only works in a server.", ephemeral: true);
            return;
        }

        var reply = await control(user);
        await RespondAsync(text: reply.Text, embed: reply.Embed, ephemeral: !reply.Success,
            allowedMentions: AllowedMentions.None,
            flags: reply.SuppressEmbeds ? MessageFlags.SuppressEmbeds : MessageFlags.None);
    }
}
