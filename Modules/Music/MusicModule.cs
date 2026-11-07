using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ModBot.Common;
using ModBot.Models;
using ModBot.Services.Settings;

namespace ModBot.Modules.Music;

/// <summary>
/// Music: a standing queue per server that anyone can add to at any time, played in turns (one song from each
/// person) once someone starts it, with votes to skip. Controlling what's playing works from the bot's voice channel
/// (moderators: anywhere). Everything here only works in the music channel (see
/// <see cref="RequireMusicChannelAttribute"/>), so it doesn't flood the main chat, and the rules live in
/// <see cref="MusicControls"/>, shared with the chatbot. The pinned menu's buttons are handled here too.
/// </summary>
[RequireContext(ContextType.Guild)]
[RequireFeature(ServerFeature.Music)]
[RequireMusicChannel]
public class MusicModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string AddModalId = "mq-add";

    private readonly MusicControls _controls;
    private readonly MusicQueueService _queue;

    public MusicModule(MusicControls controls, MusicQueueService queue)
    {
        _controls = controls;
        _queue = queue;
    }

    public enum LoopChoice
    {
        [ChoiceDisplay("Off")] Off,
        [ChoiceDisplay("Current song")] Song,
        [ChoiceDisplay("Whole queue")] Queue,
    }

    [SlashCommand("play", "Adds a song or playlist to the queue, and starts the music if it isn't playing.")]
    public Task PlayAsync(
        [Summary(description: "Song to search YouTube for, or a YouTube song/playlist link")] [MaxLength(200)] string query)
        => RunAsync((user, channel) => _controls.PlayAsync(user, channel, query), slow: true);

    [SlashCommand("add", "Adds a song or playlist to the queue without starting the music.")]
    public Task AddAsync(
        [Summary(description: "Song to search YouTube for, or a YouTube song/playlist link")] [MaxLength(200)] string query)
        => RunAsync((user, channel) => _controls.AddAsync(user, channel, query), slow: true);

    [SlashCommand("start", "Starts playing the queue in your voice channel.")]
    public Task StartAsync() => RunAsync((user, channel) => _controls.StartAsync(user, channel), slow: true);

    [SlashCommand("pause", "Pauses the current song.")]
    public Task PauseAsync() => RunAsync((user, _) => _controls.PauseAsync(user));

    [SlashCommand("resume", "Resumes a paused song.")]
    public Task ResumeAsync() => RunAsync((user, _) => _controls.ResumeAsync(user));

    [SlashCommand("skip", "Votes to skip the current song (skips it outright if you picked it).")]
    public Task SkipAsync() => RunAsync((user, _) => _controls.SkipAsync(user));

    [SlashCommand("stop", "Stops the music and leaves voice. The queue is kept for next time.")]
    public Task StopAsync() => RunAsync((user, _) => _controls.StopAsync(user));

    [SlashCommand("nowplaying", "Shows the song that's playing.")]
    public Task NowPlayingAsync() => RunAsync((user, _) => Task.FromResult(_controls.NowPlaying(user)));

    [SlashCommand("queue", "Shows the waiting songs, in turn order.")]
    public Task QueueAsync([Summary(description: "Page number")] [MinValue(1)] int page = 1)
        => RunAsync((user, _) => _controls.QueueAsync(user, page));

    [SlashCommand("remove", "Removes one of your songs from the queue (moderators: anyone's).")]
    public Task RemoveAsync([Summary(description: "Its position, as shown by /queue")] [MinValue(1)] int position)
        => RunAsync((user, _) => _controls.RemoveAsync(user, position));

    [SlashCommand("remove-mine", "Takes all your waiting songs out of the queue.")]
    public Task RemoveMineAsync() => RunAsync((user, _) => _controls.RemoveMineAsync(user));

    [SlashCommand("clearqueue", "Empties the whole queue (moderators).")]
    public Task ClearQueueAsync() => RunAsync((user, _) => _controls.ClearQueueAsync(user));

    [SlashCommand("loop", "Repeats the current song, or puts finished songs back in the queue.")]
    public Task LoopAsync([Summary(description: "What to repeat")] LoopChoice mode)
        => RunAsync((user, _) => _controls.LoopAsync(user, mode switch
        {
            LoopChoice.Song => LoopMode.Song,
            LoopChoice.Queue => LoopMode.Queue,
            _ => LoopMode.Off,
        }));

    [SlashCommand("music-menu", "Posts the music menu here and pins it (replacing any older one).")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task MusicMenuAsync()
    {
        await DeferAsync(ephemeral: true);

        if (ChannelAccess.WhyCantPostPanel(Context.Guild.CurrentUser, Context.User as IGuildUser, Context.Channel,
                "the music menu") is { } problem)
        {
            await FollowupAsync(problem, ephemeral: true);
            return;
        }

        var (pinned, error) = await _queue.PostMenuAsync((ITextChannel)Context.Channel);
        await FollowupAsync(error ?? "Posted the music menu" + (pinned
            ? " and pinned it."
            : " (I couldn't pin it - give me **Pin Messages**, or pin it yourself)."), ephemeral: true);
    }

    // The pinned menu's buttons. Their IDs are fixed, so a menu keeps working across restarts.

    [ComponentInteraction(MusicQueueService.AddButton)]
    public Task AddButtonAsync() => RespondWithModalAsync<AddSongModal>(AddModalId);

    [ModalInteraction(AddModalId)]
    public Task AddSubmittedAsync(AddSongModal modal)
        => ButtonAsync((user, channel) => _controls.AddAsync(user, channel, modal.Query ?? ""), alwaysPrivate: true);

    [ComponentInteraction(MusicQueueService.PlayButton)]
    public Task PlayButtonAsync() => ButtonAsync((user, channel) => _controls.StartAsync(user, channel));

    [ComponentInteraction(MusicQueueService.SkipButton)]
    public Task SkipButtonAsync() => ButtonAsync((user, _) => _controls.SkipAsync(user));

    [ComponentInteraction(MusicQueueService.PauseButton)]
    public Task PauseButtonAsync() => ButtonAsync((user, _) => _controls.PauseAsync(user));

    [ComponentInteraction(MusicQueueService.ResumeButton)]
    public Task ResumeButtonAsync() => ButtonAsync((user, _) => _controls.ResumeAsync(user));

    [ComponentInteraction(MusicQueueService.StopButton)]
    public Task StopButtonAsync() => ButtonAsync((user, _) => _controls.StopAsync(user));

    /// <summary>"Remove mine": a private list of the person's waiting songs, to take out one, or all of them.</summary>
    [ComponentInteraction(MusicQueueService.MineButton)]
    public async Task MineButtonAsync()
    {
        var songs = await _queue.SongsOfAsync(Context.Guild.Id, Context.User.Id);
        if (songs.Count == 0)
        {
            await RespondAsync("You don't have any songs waiting.", ephemeral: true);
            return;
        }

        // A list holds 25 choices: up to 24 songs, and "all of them".
        var pick = new SelectMenuBuilder()
            .WithCustomId(MusicQueueService.MinePick)
            .WithPlaceholder("Pick a song to take out");
        foreach (var (position, song) in songs.Take(24))
        {
            pick.AddOption(TextTools.Truncate(song.Title, 100), song.Id.ToString(),
                $"#{position} in the queue · {MusicFormatting.SongLength(song)}");
        }

        pick.AddOption(songs.Count == 1 ? "All of my songs (just this one)" : $"All {songs.Count} of my songs",
            MusicQueueService.AllMine, emote: new Emoji("🗑️"));
        await RespondAsync(songs.Count > 24 ? "Your first 24 waiting songs:" : "Your waiting songs:",
            components: new ComponentBuilder().WithSelectMenu(pick).Build(), ephemeral: true);
    }

    [ComponentInteraction(MusicQueueService.MinePick)]
    public Task MinePickedAsync(string[] picked)
        => ButtonAsync((user, _) => picked.FirstOrDefault() is { } choice && Guid.TryParse(choice, out var id)
            ? _queue.RemoveSongAsync(user, id)
            : _controls.RemoveMineAsync(user), alwaysPrivate: true);

    /// <summary>Runs a command and replies: publicly when it worked (everyone listening should know), privately when
    /// it didn't.</summary>
    /// <param name="slow">Looking up songs or joining voice can take longer than Discord's 3 seconds, so those
    /// acknowledge first.</param>
    private async Task RunAsync(Func<SocketGuildUser, ITextChannel, Task<MusicReply>> control, bool slow = false)
    {
        if (Context.User is not SocketGuildUser user || Context.Channel is not ITextChannel channel)
        {
            await RespondAsync("Music only works in a server text channel.", ephemeral: true);
            return;
        }

        if (!slow)
        {
            var quick = await control(user, channel);
            await RespondAsync(text: quick.Text, embed: quick.Embed, ephemeral: !quick.Success,
                allowedMentions: AllowedMentions.None,
                flags: quick.SuppressEmbeds ? MessageFlags.SuppressEmbeds : MessageFlags.None);
            return;
        }

        // Shown to everyone, since most of these work; problems are then public too, which is fine.
        await DeferAsync();
        var reply = await control(user, channel);
        await FollowupAsync(text: reply.Text, embed: reply.Embed, allowedMentions: AllowedMentions.None,
            flags: reply.SuppressEmbeds ? MessageFlags.SuppressEmbeds : MessageFlags.None);
    }

    /// <summary>Runs a menu button. The menu shows the result, so replies are private unless everyone listening
    /// should hear about it (the music started or stopped, a song was skipped or paused).</summary>
    private async Task ButtonAsync(Func<SocketGuildUser, ITextChannel, Task<MusicReply>> action, bool alwaysPrivate = false)
    {
        if (Context.User is not SocketGuildUser user || Context.Channel is not ITextChannel channel)
        {
            await RespondAsync("The music menu only works in a server text channel.", ephemeral: true);
            return;
        }

        // Acknowledged without a message of its own, so the follow-up can be private or public. If the action throws,
        // DiscordBotService.OnInteractionExecutedAsync logs it and sends the "something went wrong" follow-up.
        await DeferAsync();
        var reply = await action(user, channel);
        await FollowupAsync(text: reply.Text, embed: reply.Embed,
            ephemeral: alwaysPrivate || !reply.Success || !reply.Announce,
            allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
    }

    public class AddSongModal : IModal
    {
        public string Title => "Add a song to the queue";

        [InputLabel("Song name or YouTube link")]
        [ModalTextInput("query", TextInputStyle.Short, placeholder: "e.g. mr brightside the killers", maxLength: 200)]
        public string? Query { get; set; }
    }
}
