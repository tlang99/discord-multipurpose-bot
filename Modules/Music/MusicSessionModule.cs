using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace ModBot.Modules.Music;

/// <summary>
/// /session and the session menu's buttons. A music session lets everyone build up a queue first, then plays it in
/// turns (one song from each person, round and round), with votes to skip. Songs also go in with /play or by asking
/// the chatbot. The rules live in <see cref="MusicSessionService"/>.
/// </summary>
[RequireContext(ContextType.Guild)]
[RequireMusicChannel]
[Group("session", "Music sessions: build a queue together, then take turns.")]
public class MusicSessionModule : InteractionModuleBase<SocketInteractionContext>
{
    private const string AddModalPrefix = "msession-add";

    private readonly MusicSessionService _sessions;

    public MusicSessionModule(MusicSessionService sessions)
    {
        _sessions = sessions;
    }

    [SlashCommand("open", "Opens a music session here: everyone adds songs, then they play in turns.")]
    public async Task OpenAsync()
    {
        if (Context.User is not SocketGuildUser user || Context.Channel is not ITextChannel channel)
        {
            await RespondAsync("Music sessions only work in a server text channel.", ephemeral: true);
            return;
        }

        await DeferAsync();
        var reply = await _sessions.OpenAsync(user, channel);
        await FollowupAsync(reply.Text, allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
    }

    [SlashCommand("start", "Starts playing the session's songs (host only), from your voice channel.")]
    public Task StartAsync() => RunAsync(true, (session, user) => _sessions.StartAsync(session, user));

    [SlashCommand("end", "Ends the music session (host or moderator).")]
    public Task EndAsync() => RunAsync(false, (session, user) => _sessions.EndAsync(session, user));

    [SlashCommand("leave", "Leaves the music session and takes out your waiting songs.")]
    public Task LeaveAsync() => RunAsync(false, (session, user) => _sessions.LeaveAsync(session, user));

    [SlashCommand("menu", "Posts the session menu again at the bottom of the channel.")]
    public Task MenuAsync() => RunAsync(false, (session, _) => _sessions.ShowMenuAsync(session));

    private async Task RunAsync(bool slow, Func<MusicSession, SocketGuildUser, Task<MusicReply>> action)
    {
        if (Context.User is not SocketGuildUser user)
        {
            await RespondAsync("Music sessions only work in a server.", ephemeral: true);
            return;
        }

        if (_sessions.Get(Context.Guild.Id) is not { } session)
        {
            await RespondAsync("There's no music session right now - open one with `/session open`.", ephemeral: true);
            return;
        }

        if (slow)
        {
            // Joining voice can take longer than Discord's 3 seconds.
            await DeferAsync();
            var slowReply = await action(session, user);
            await FollowupAsync(slowReply.Text, ephemeral: !slowReply.Success, allowedMentions: AllowedMentions.None,
                flags: MessageFlags.SuppressEmbeds);
            return;
        }

        var reply = await action(session, user);
        await RespondAsync(reply.Text, ephemeral: !reply.Success, allowedMentions: AllowedMentions.None,
            flags: MessageFlags.SuppressEmbeds);
    }

    // Buttons on the menu. Their IDs are "msession:<button>:<session id>", so an old menu's buttons can tell the
    // session they belonged to is over. ignoreGroupNames: the IDs aren't prefixed with "session".

    [ComponentInteraction(MusicSessionService.ButtonPrefix + ":" + MusicSessionService.AddButton + ":*",
        ignoreGroupNames: true)]
    public async Task AddButtonAsync(string sessionId)
    {
        if (_sessions.GetForButton(Context.Guild.Id, sessionId) is null)
        {
            await RespondAsync("This session is over - open a new one with `/session open`.", ephemeral: true);
            return;
        }

        await RespondWithModalAsync<AddSongModal>($"{AddModalPrefix}:{sessionId}");
    }

    [ModalInteraction(AddModalPrefix + ":*", ignoreGroupNames: true)]
    public Task AddSubmittedAsync(string sessionId, AddSongModal modal)
        => ButtonAsync(sessionId, (session, user) => _sessions.AddAsync(session, user, modal.Query ?? ""),
            alwaysPrivate: true);

    [ComponentInteraction(MusicSessionService.ButtonPrefix + ":" + MusicSessionService.StartButton + ":*",
        ignoreGroupNames: true)]
    public Task StartButtonAsync(string sessionId)
        => ButtonAsync(sessionId, (session, user) => _sessions.StartAsync(session, user));

    [ComponentInteraction(MusicSessionService.ButtonPrefix + ":" + MusicSessionService.SkipButton + ":*",
        ignoreGroupNames: true)]
    public Task SkipButtonAsync(string sessionId)
        => ButtonAsync(sessionId, (session, user) => _sessions.VoteSkipAsync(session, user));

    [ComponentInteraction(MusicSessionService.ButtonPrefix + ":" + MusicSessionService.LeaveButton + ":*",
        ignoreGroupNames: true)]
    public Task LeaveButtonAsync(string sessionId)
        => ButtonAsync(sessionId, (session, user) => _sessions.LeaveAsync(session, user));

    [ComponentInteraction(MusicSessionService.ButtonPrefix + ":" + MusicSessionService.EndButton + ":*",
        ignoreGroupNames: true)]
    public Task EndButtonAsync(string sessionId)
        => ButtonAsync(sessionId, (session, user) => _sessions.EndAsync(session, user));

    /// <summary>Runs a menu action. The menu itself shows the result, so replies are private unless everyone
    /// listening should hear about it (the session started or ended, a song was skipped).</summary>
    private async Task ButtonAsync(string sessionId, Func<MusicSession, SocketGuildUser, Task<MusicReply>> action,
        bool alwaysPrivate = false)
    {
        if (Context.User is not SocketGuildUser user)
        {
            return;
        }

        // Acknowledged without a message of its own (joining voice or looking up a song can take a few seconds), so
        // the follow-up below can be private or public.
        await DeferAsync();

        var reply = _sessions.GetForButton(Context.Guild.Id, sessionId) is { } session
            ? await action(session, user)
            : MusicReply.Fail("This session is over - open a new one with `/session open`.");

        await FollowupAsync(reply.Text, ephemeral: alwaysPrivate || !reply.Success || !reply.Announce,
            allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressEmbeds);
    }

    public class AddSongModal : IModal
    {
        public string Title => "Add a song to the session";

        [InputLabel("Song name or YouTube link")]
        [ModalTextInput("query", TextInputStyle.Short, placeholder: "e.g. mr brightside the killers", maxLength: 200)]
        public string? Query { get; set; }
    }
}
