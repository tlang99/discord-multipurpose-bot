using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Modules.Lfg;
using ModBot.Modules.Music;
using ModBot.Models;
using ModBot.Modules.Polls;
using ModBot.Modules.Settings;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
using ModBot.Services.News;
using ModBot.Services.Polls;
using ModBot.Services.Reminders;
using ModBot.Services.Search;
using ModBot.Services.Settings;
using ModBot.Services.Storage;
using ModBot.Services.Voice;
using ModBot.Modules.Voice;

namespace ModBot.EventHandlers;

/// <summary>
/// The chatbot: answers messages that @mention the bot or reply to one of its messages, using the recent messages in
/// the channel as context, and requests said in voice after its name (<see cref="HandleVoiceRequestAsync"/>).
/// Questions and answers are both checked against the server's word filter, replies never ping anyone but the asker,
/// and each person has a cooldown so one user can't run up usage.
/// </summary>
public partial class ChatHandler
{
    private const int DiscordMessageLimit = 2000;
    private const int MaxHistoryMessages = 50;

    /// <summary>Always part of the system prompt, after the personality, so no personality can switch them off.
    /// "Reply only as yourself" in particular stops small models scripting a whole conversation.</summary>
    private const string GroundRules =
        "Whatever your personality, always follow these rules: Keep answers short: usually a few sentences, and well " +
        "under 1,500 characters unless someone asks for detail. Reply only as yourself, in your own words. Never " +
        "start your reply with a name and a colon, and never write lines for anyone else. Use Discord markdown " +
        "sparingly. Never write @everyone or @here, and don't try to ping people. Never claim you played, skipped, " +
        "paused or stopped a song, changed the queue, started the music, posted an LFG, set a reminder or posted or " +
        "ended a poll: those requests are handled separately, so if someone asks how, tell them to ask you directly " +
        "(e.g. \"play <song>\", \"add <song> to the queue\", \"skip this\", \"show the queue\", \"start the music\", " +
        "\"make an LFG for ranked\", \"remind me in 20 minutes to check the oven\" - reminders need a time - \"make a " +
        "poll: what should we play? ranked, casual or zombies\" or \"end my poll\") or see /help. You can't look " +
        "things up online by yourself: for anything that needs current information (news, patch notes, prices, " +
        "scores), say you don't know for sure and tell them to ask you to \"search for <topic>\" or use /search. How the " +
        "music works: there's one standing queue anyone can add to any time, even when nothing's playing; songs play " +
        "in turns, one from each person; skipping a song takes votes from more than half the listeners, unless you " +
        "picked it; and there's a pinned music menu with buttons in the music channel.";

    private readonly IChatService _chat;
    private readonly ChatActionParser _actionParser;
    private readonly MusicControls _musicControls;
    private readonly MusicQueueService _musicQueue;
    private readonly LfgPoster _lfgPoster;
    private readonly ReminderService _reminders;
    private readonly PollPoster _polls;
    private readonly IGuildSettingsStore _settingsStore;
    private readonly ServerSettingsService _serverSettings;
    private readonly ServerFeatureSwitch _featureSwitch;
    private readonly NewsNotifications _newsNotifications;
    private readonly ChatPersonality _personality;
    private readonly VoiceListener _voice;
    private readonly WebSearchAnswerer _search;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatHandler> _logger;
    private readonly UserCooldowns _questions;

    public ChatHandler(IChatService chat, ChatActionParser actionParser,
        MusicControls musicControls, MusicQueueService musicQueue, LfgPoster lfgPoster,
        ReminderService reminders, PollPoster polls,
        IGuildSettingsStore settingsStore, ServerSettingsService serverSettings, ServerFeatureSwitch featureSwitch,
        NewsNotifications newsNotifications,
        ChatPersonality personality, VoiceListener voice, WebSearchAnswerer search, IOptions<ChatOptions> options,
        ILogger<ChatHandler> logger)
    {
        _search = search;
        _chat = chat;
        _actionParser = actionParser;
        _musicControls = musicControls;
        _musicQueue = musicQueue;
        _lfgPoster = lfgPoster;
        _reminders = reminders;
        _polls = polls;
        _settingsStore = settingsStore;
        _serverSettings = serverSettings;
        _featureSwitch = featureSwitch;
        _newsNotifications = newsNotifications;
        _personality = personality;
        _voice = voice;
        _options = options.Value;
        _questions = new UserCooldowns(_options.CooldownSeconds);
        _logger = logger;
    }

    public async Task HandleMessageAsync(SocketMessage rawMessage)
    {
        if (!_options.IsEnabled ||
            rawMessage is not SocketUserMessage message ||
            message.Author.IsBot || message.Author.IsWebhook ||
            message.Channel is not SocketTextChannel channel)
        {
            return;
        }

        var guild = channel.Guild;
        var bot = guild.CurrentUser;
        var addressed = message.MentionedUsers.Any(u => u.Id == bot.Id) || message.ReferencedMessage?.Author.Id == bot.Id;
        if (!addressed)
        {
            return;
        }

        if (!ChannelAccess.CanPost(bot, channel) ||
            await AllowedAsync(guild, message.Author, message.Content) is not { } allowed)
        {
            return;
        }

        await RespondAsync(allowed, new ChatRequest(channel, message, message.Author,
            Describe(message, guild, bot.Id, noteAttachments: false), Describe(message, guild, bot.Id)));
    }

    /// <summary>An answer to a spoken request: what to say (after the word filter), and whether it's in chat already
    /// (a web search's embed, or an answer that couldn't be said aloud).</summary>
    /// <param name="Heard">An action that worked (see <see cref="ChatRequest.Heard"/>): confirm with a chime, not
    /// the words.</param>
    public sealed record VoiceAnswer(string Text, bool InChat, bool Heard = false);

    /// <summary>
    /// Something said in voice after the bot's name (<see cref="VoiceListener"/>), handled exactly like the same words
    /// typed as an @mention by <paramref name="speaker"/>: the same switches, word filter, cooldown and actions.
    /// <para>With <paramref name="aloud"/>, the answer is only spoken, not posted: nothing goes in chat but what an
    /// action posts itself (a poll, an LFG card, a web search's embed under "🎙️ @name searched: …"). Otherwise, what
    /// was heard is posted ("🎙️ @name: …") and the answer replies to it, as typed.</para>
    /// Returns the answer, or null if there was none.
    /// </summary>
    /// <param name="earlier">In a voice conversation (see <see cref="VoiceConversation"/>): what was said and answered
    /// so far, for the chatbot to follow on from. Its requests skip the cooldown (VoiceListener holds
    /// them to the shorter Voice:FollowUpCooldownSeconds): a back-and-forth is the point.</param>
    public async Task<VoiceAnswer?> HandleVoiceRequestAsync(SocketGuildUser speaker, SocketTextChannel channel,
        string request, bool aloud, IReadOnlyList<ChatTurn>? earlier = null)
    {
        var guild = channel.Guild;
        if (!ChannelAccess.CanPost(guild.CurrentUser, channel))
        {
            await StopListeningCantPostAsync(channel);
            return null;
        }

        // The speaker has to be able to post where the answer goes themselves: saying it mustn't put a poll or LFG in
        // a channel they couldn't type in.
        if (!_options.IsEnabled || !ChannelAccess.CanPost(speaker, channel) ||
            await AllowedAsync(guild, speaker, request) is not { } allowed ||
            !allowed.Server.IsEnabled(ServerFeature.VoiceCommands))
        {
            return null;
        }

        // The cooldown is checked (and counted) here, before anything's posted: typed, a request during it gets a ⏳,
        // but said, there'd be nothing to put that on but a fresh post of what was heard. So it's just dropped, and a
        // chatty speaker or a noisy mic can't flood the channel.
        if (earlier is not { Count: > 0 } && IsOnCooldown(guild.Id, speaker.Id))
        {
            return null;
        }

        // What was heard is posted in the bot's name, so it follows the rules for the bot's own posts: the server's word
        // list and invite blocking always apply, auto-mod on or off (a mod's own words excepted, as for their polls and
        // reminders). Typed, the member's own message would be up to auto-mod; said, the bot would be the one posting.
        if (TextFilter.AppliesToBotPost(speaker) && TextFilter.FindViolation(request, allowed.Settings) is not null)
        {
            return null;
        }

        IUserMessage? heard = null;
        if (!aloud)
        {
            heard = await PostHeardAsync(speaker, channel, request);
            if (heard is null)
            {
                await StopListeningCantPostAsync(channel);
                return null;
            }
        }

        var said = new ChatRequest(channel, heard, speaker, request, request, Spoken: true, Earlier: earlier);
        await RespondAsync(allowed, said);
        return said.Answer is { } answer ? new VoiceAnswer(answer, said.AnswerInChat, said.Heard) : null;
    }

    /// <summary>For an answer that was to be spoken but couldn't be (text-to-speech failed): posts what was heard and
    /// the answer in reply, as if it had never been meant to be spoken. <paramref name="answer"/> has been through the
    /// word filter already.</summary>
    public async Task PostUnspokenAnswerAsync(SocketGuildUser speaker, SocketTextChannel channel, string request,
        string answer)
    {
        if (await PostHeardAsync(speaker, channel, request) is { } heard)
        {
            await ReplyAsync(new ChatRequest(channel, heard, speaker, request, request, Spoken: true), answer);
        }
    }

    /// <summary>Posts "🎙️ @name: what they said"; null (logged) if it couldn't be.</summary>
    private async Task<IUserMessage?> PostHeardAsync(SocketGuildUser speaker, SocketTextChannel channel, string said)
    {
        try
        {
            // No link previews: a misheard or spoken link shouldn't turn into a preview card under the bot's name.
            return await channel.SendMessageAsync(HeardText(speaker, said), allowedMentions: AllowedMentions.None,
                flags: MessageFlags.SuppressEmbeds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't post a voice request in channel {ChannelId}.", channel.Id);
            return null;
        }
    }

    /// <summary>
    /// The bot can't post where voice requests are answered any more (a permission changed mid-session): stop
    /// listening rather than sit in the channel ignoring everyone. It can't say so there, so leaving the voice channel
    /// is the sign; the log says why.
    /// </summary>
    private async Task StopListeningCantPostAsync(SocketTextChannel channel)
    {
        _logger.LogWarning("Stopped listening in guild {GuildId}: can't post voice replies in #{Channel} any more.",
            channel.Guild.Id, channel.Name);
        await _voice.StopAndStayOutAsync(channel.Guild.Id); // or voice auto-join could rejoin and hit the same wall
    }

    /// <summary>A request to the chatbot, typed or said.</summary>
    /// <param name="Message">What replies answer: the typed message, or the bot's post of what it heard. Null for a
    /// spoken request answered aloud: nothing was posted, and the answer isn't either (see <see cref="Aloud"/>).</param>
    /// <param name="Words">Just what they wrote (or said), for working out actions.</param>
    /// <param name="Question">What they wrote with a note for attachments, for the chatbot to answer.</param>
    /// <param name="Spoken">Said in voice: its cooldown was already counted before posting what was heard.</param>
    /// <param name="Earlier">A voice conversation so far, oldest first (null for none).</param>
    private sealed record ChatRequest(SocketTextChannel Channel, IUserMessage? Message, IUser Author, string Words,
        string Question, bool Spoken = false, IReadOnlyList<ChatTurn>? Earlier = null)
    {
        /// <summary>Answered by voice only: replies aren't posted, except an embed (a web search's).</summary>
        public bool Aloud => Message is null;

        /// <summary>The answer: its text, or for an answer that's all in an embed (a web search), the embed's. Set by
        /// <see cref="ReplyAsync"/>; null if there was none.</summary>
        public string? Answer { get; set; }

        /// <summary>Whether the answer was posted in chat too.</summary>
        public bool AnswerInChat { get; set; }

        /// <summary>An action that worked (a song played, a reminder set, an LFG or poll posted): said in voice, the
        /// "done" chime answers it instead of reading the reply out. Not set for chat, or for what was asked about
        /// (what's playing, the queue, a search), whose answer is the point.</summary>
        public bool Heard { get; set; }
    }

    /// <summary>True (after a ⏳ on the message) if the asker has to wait. Counts the request otherwise.</summary>
    private async Task<bool> MustWaitAsync(ChatRequest request)
    {
        if (request.Spoken || request.Message is not { } message ||
            !IsOnCooldown(request.Channel.Guild.Id, request.Author.Id))
        {
            return false;
        }

        await ReactAsync(message, "⏳");
        return true;
    }

    /// <summary>The server's settings, when <paramref name="author"/> gets an answer at all.</summary>
    private sealed record Allowed(GuildSettings Settings, ServerSettings Server, bool IsAdmin);

    /// <summary>Null when the request gets no answer: it breaks the word filter (auto-mod is about to remove it), or
    /// the chatbot is off here and the asker isn't an admin. An admin still gets through, so they can turn it back on
    /// by asking ("@bot enable the chatbot").</summary>
    private async Task<Allowed?> AllowedAsync(SocketGuild guild, IUser author, string text)
    {
        var settings = await _settingsStore.GetAsync(guild.Id);
        if (TextFilter.AppliesTo(settings, author as SocketGuildUser) &&
            TextFilter.FindViolation(text, settings) is not null)
        {
            return null;
        }

        var server = await _serverSettings.GetAsync(guild.Id);
        var isAdmin = author is SocketGuildUser member && ServerFeatureSwitch.CanChange(member);
        return server.IsEnabled(ServerFeature.Chat) || isAdmin ? new Allowed(settings, server, isAdmin) : null;
    }

    private async Task RespondAsync(Allowed allowed, ChatRequest request)
    {
        var (channel, message, asker, words, question, _, earlier) = request;
        var (settings, server, isAdmin) = allowed;
        var guild = channel.Guild;
        var bot = guild.CurrentUser;

        // Admin switches from chat: a feature on/off ("disable polls") or news notifications ("mute news
        // notifications"), for this server only.
        var change = SettingsRequestReader.Read(words);
        var newsNotify = change is null ? await _newsNotifications.ReadForGuildAsync(guild.Id, words) : null;
        if (asker is SocketGuildUser member && (change is not null || newsNotify is not null))
        {
            if (await MustWaitAsync(request))
            {
                return;
            }

            if (request.Spoken)
            {
                // Said, it would run as whoever's mic picked it up: a friend in the room, a soundboard, or a
                // mishearing ("enable" for "disable") could change the server's settings as an admin.
                await ReplyAsync(request, "Server settings can only be changed by typing, so nobody can change them " +
                                          "by talking near an admin's mic. Type it, or use `/settings`.");
            }
            else if (!isAdmin)
            {
                await ReplyAsync(request, change is not null
                    ? "Only server admins (**Manage Server**) can turn features on or off. See `/settings`."
                    : "Only server admins (**Manage Server**) can change news notifications. See `/news notify`.");
            }
            else
            {
                // Each switch saves, logs who changed it, and words its own reply.
                await ReplyAsync(request, change is not null
                    ? await _featureSwitch.SetAsync(guild, change.Feature, change.On, member)
                    : await _newsNotifications.SetAsync(guild.Id, newsNotify!.Feed, newsNotify.Silent, member));
            }

            return;
        }

        if (!server.IsEnabled(ServerFeature.Chat))
        {
            return; // an admin, but not asking to switch something
        }

        // "Listen in voice" / "stop listening", typed or said. "Leave voice" is only about listening while the bot is
        // listening; otherwise it's the music's (MusicCommandReader: stop and leave).
        if (VoiceRequestReader.Read(words) is { } voiceRequest && asker is SocketGuildUser voiceAsker &&
            (voiceRequest == VoiceRequest.Listen || _voice.IsListening(guild.Id)))
        {
            if (await MustWaitAsync(request))
            {
                return;
            }

            // As with /voice: only starting needs the feature on, stopping always works.
            var answer = voiceRequest == VoiceRequest.StopListening
                ? await _voice.StopForAsync(voiceAsker)
                : !server.IsEnabled(ServerFeature.VoiceCommands)
                    ? ServerSettingsService.TurnedOff(ServerFeature.VoiceCommands)
                    : await _voice.StartAsync(voiceAsker, channel);
            _logger.LogInformation("Chat action {Action} for {User} ({UserId}) in #{Channel}.", voiceRequest,
                asker.Username, asker.Id, channel.Name);
            if (answer.Length > 0)
            {
                await ReplyAsync(request, answer);
            }

            return;
        }

        if (question.Length == 0)
        {
            await ReplyAsync(request, $"👋 Ask me something after the mention, like \"@{bot.DisplayName} what should " +
                                      "we play tonight?\"");
            return;
        }

        if (await MustWaitAsync(request))
        {
            return;
        }

        string? reply = null;
        ChatAction? action = null;
        // No "typing…" for an answer that'll only be spoken: nothing would follow it in chat.
        using (request.Aloud ? null : channel.EnterTypingState())
        {
            // How many requests reach the model at once is limited inside the chat service, shared with news.
            try
            {
                // "Play X" / "make an LFG" are done, not chatted about. Only the asker's own message is looked at.
                // Web search has its own switch and is asked for in so many words, so it works with chat actions off
                // too (the ground rules tell people to ask for one).
                if (asker is SocketGuildUser && words.Length > 0)
                {
                    action = _options.Actions && server.IsEnabled(ServerFeature.ChatActions)
                        ? await _actionParser.ParseAsync(words, server.LfgGame)
                        : ChatActionParser.ReadSearch(words);
                }

                if (action is null)
                {
                    var (prompt, speakers) = await BuildPromptAsync(channel, message, asker, guild, bot, question);
                    reply = await _chat.ReplyAsync(await BuildSystemPromptAsync(guild, bot),
                        [.. earlier ?? [], new ChatTurn(ChatRole.User, prompt)]);
                    reply = RemoveScriptedLines(reply, speakers);
                }
            }
            catch (ChatServiceException ex)
            {
                _logger.LogWarning(ex, "Chat reply failed in guild {GuildId} (model {Model} at {Url}).",
                    guild.Id, _options.Model, _options.OllamaUrl);
                reply = ex.StatusCode switch
                {
                    401 or 403 => "My AI connection isn't set up right - a server admin needs to check its API key.",
                    404 => "The AI model in my settings wasn't found - a server admin needs to check it.",
                    _ => "I couldn't come up with an answer right now - try again in a bit.",
                };
            }

            if (action is not null && reply is null)
            {
                string text;
                var suppressEmbeds = false;
                Embed? embed = null;
                string? spoken = null; // what to say, when it isn't the text
                var outcome = "done";
                try
                {
                    // Same switches as the slash commands: a feature the server turned off isn't done from chat either.
                    if (FeatureFor(action) is { } needed && !server.IsEnabled(needed))
                    {
                        text = ServerSettingsService.TurnedOff(needed);
                        outcome = "feature turned off";
                    }
                    else if (action is MusicControlAction or UnclearMusicAction)
                    {
                        var music = await RunMusicControlAsync(action, (SocketGuildUser)asker, channel,
                            request.Spoken);
                        (text, suppressEmbeds, embed) = (music.Text ?? "", music.SuppressEmbeds, music.Embed);
                        // Done is answered with a chime in voice; what was asked about (what's on, the queue, the menu)
                        // is said.
                        request.Heard = music.Success && action is MusicControlAction
                        {
                            Request.Command: not (MusicCommand.NowPlaying or MusicCommand.ShowQueue or
                            MusicCommand.ShowMenu),
                        };
                    }
                    else if (action is WebSearchAction search)
                    {
                        // The answer in the server's personality, its sources in an embed under it.
                        var found = _search.TryStart(guild.Id, asker.Id) is { } reason
                            ? new SearchAnswer(reason, null)
                            : await _search.AnswerAsync(guild.Id, guild.Name, bot.DisplayName, search.Query,
                                quick: request.Aloud);
                        (text, embed) = (found.Text, found.Embed);
                        // Said, it's posted like /search (where Discord shows who searched for what); the answer
                        // is read aloud.
                        if (request.Aloud && embed is not null)
                        {
                            (text, spoken) = (SearchedText(asker, search.Query), embed.Description);
                        }
                    }
                    else
                    {
                        (text, suppressEmbeds) = await RunActionAsync(action, (SocketGuildUser)asker, channel,
                            message, request);
                    }
                }
                catch (Exception ex)
                {
                    // Lavalink or Discord failing mid-action: say so instead of leaving the request unanswered.
                    _logger.LogWarning(ex, "Chat action {Action} failed in guild {GuildId}.", action.GetType().Name, guild.Id);
                    text = "Something went wrong doing that - try again in a bit, or use the slash command.";
                    outcome = "failed";
                }

                // The action and how it went, never the reply text: replies repeat what people asked ("I'll remind you
                // to..."), and what people say to the bot stays out of the log files.
                _logger.LogInformation("Chat action {Action} for {User} ({UserId}) in #{Channel}: {Outcome}.",
                    action is MusicControlAction { Request.Command: var command }
                        ? $"MusicControl ({command})"
                        : action.GetType().Name.Replace("Action", ""), asker.Username, asker.Id,
                    channel.Name, outcome);

                // A posted poll is its own answer (it's a reply to the request); every other action replies.
                if (action is CreatePollAction && text.Length == 0)
                {
                    return;
                }

                // Song and playlist titles, and search results and answers, are third-party text, so action replies
                // follow the word rules too.
                if (TextFilter.FindViolation(text + "\n" + MessageTools.EmbedText(embed, withLinks: action is WebSearchAction), settings) is not null)
                {
                    text = action is PlayMusicAction or MusicControlAction
                        ? "My reply named a song whose title breaks this server's word filter, so I won't repeat it " +
                          "here. Check `/queue` to see what happened."
                        : "My reply broke this server's word filter, so I'm keeping the details to myself.";
                    suppressEmbeds = false;
                    embed = null;
                    spoken = null;
                }

                await ReplyAsync(request, text, suppressEmbeds, embed, spoken);
                return;
            }
        }

        reply ??= "I couldn't come up with an answer right now - try again in a bit.";

        reply = TextTools.Truncate(reply, DiscordMessageLimit);

        // The model can be talked into saying anything, so its answers always follow the server's word list (auto-mod
        // on or off: that switch is only about scanning members' messages).
        if (TextFilter.FindViolation(reply, settings) is not null)
        {
            reply = "I came up with an answer, but it broke this server's word filter, so I'm keeping it to myself. " +
                    "Try asking another way.";
        }

        // Not what was said, only that it happened: the conversation itself stays out of the log.
        _logger.LogInformation("Chat answered {User} ({UserId}) in #{Channel} ({Length} characters).",
            asker.Username, asker.Id, channel.Name, reply.Length);
        await ReplyAsync(request, reply);
    }

    /// <summary>
    /// Does what the message asked, as the person who asked, through the same code as /play, /lfg and /remind (so the
    /// same rules apply: in a voice channel to play, one open LFG each, the reminder limits). Music and LFG posts go to
    /// their own channels wherever the request was made; reminders are delivered here, replying to the request.
    /// Polls are posted right here, as a reply to the request. Returns the reply (empty when the action already
    /// answered), and whether to hide link previews in it.
    /// </summary>
    private async Task<(string Text, bool SuppressEmbeds)> RunActionAsync(ChatAction action, SocketGuildUser asker,
        SocketTextChannel channel, IUserMessage? request, ChatRequest said)
    {
        var guild = channel.Guild;
        var requestMessageId = request?.Id;
        switch (action)
        {
            case PlayMusicAction play:
            {
                var music = await _serverSettings.MusicChannelAsync(guild);
                if (music.Or(channel) is not { } musicChannel)
                {
                    return (music.Missing("Music goes"), false);
                }

                // Into the standing queue, in the person's turns. "Play" also starts the music if it isn't playing.
                var result = play.StartPlaying
                    ? await _musicControls.PlayAsync(asker, musicChannel, play.Query)
                    : await _musicControls.AddAsync(asker, musicChannel, play.Query);
                var where = result.Success && musicChannel.Id != channel.Id ? $" (music's in {musicChannel.Mention})" : "";
                said.Heard = result.Success; // done: a chime says so in voice
                return ((result.Text ?? "Done.") + where, result.SuppressEmbeds);
            }

            case CreateLfgAction lfg:
            {
                var lfgWhere = await _serverSettings.LfgChannelAsync(guild);
                if (lfgWhere.Or(channel) is not { } lfgChannel)
                {
                    return (lfgWhere.Missing("LFG posts go"), false);
                }

                // Posting for someone is only allowed where they could post themselves.
                if (!ChannelAccess.CanPost(asker, lfgChannel))
                {
                    return ($"You can't post in {lfgChannel.Mention}, so I can't post an LFG there for you.", false);
                }

                // No note from chat: free text on a public card is left to /lfg.
                var result = await _lfgPoster.PostAsync(asker, lfgChannel, lfg.Players, lfg.Details, note: null);
                said.Heard = result.Success;
                return result.Success && result.CardLink is not null
                    ? ($"{result.Message}\n[Go to your LFG in #{lfgChannel.Name}]({result.CardLink}) - use `/lfg` " +
                       "if you want to add a note.", true)
                    : (result.Message, false);
            }

            case SetReminderAction reminder:
            {
                if (reminder.Text.Length == 0)
                {
                    return ("What should I remind you about? Try \"remind me in 20 minutes to check the oven\".", false);
                }

                // Same exemption as auto-mod: moderators aren't held to the word filter.
                var result = await _reminders.CreateAtAsync(guild.Id, channel.Id, asker.Id, reminder.DueAt,
                    reminder.Text, applyWordFilter: !TextFilter.IsExempt(asker),
                    sourceMessageId: requestMessageId);
                said.Heard = result.Success;
                return (result.Message, false);
            }

            case CreatePollAction poll:
            {
                var check = PollRules.Check(poll.Question, poll.Answers, poll.Hours ?? _polls.DefaultHours,
                    poll.AllowMultiple);
                if (check.Draft is not { } draft)
                {
                    return (check.Problem!, false);
                }

                // The poll is the answer; only a problem gets a reply of its own.
                var problem = await _polls.PostReplyAsync(asker, channel, draft, request, poll.FromModel);
                said.Heard = problem is null;
                return (problem ?? "", false);
            }

            case UnclearPollAction:
                return ("I couldn't tell what the poll should ask, or what the choices are. Try \"make a poll: what " +
                        "should we play? ranked, casual or zombies\", or use `/poll`.", false);

            case EndPollAction end:
            {
                // The reply's own target, which is there even when Discord doesn't attach the message itself.
                var ended = await _polls.EndRequestedAsync(asker, channel, request?.Reference?.MessageId.ToNullable(),
                    end.SaidMine);
                said.Heard = ended.StartsWith(PollPoster.EndedText, StringComparison.Ordinal);
                return (ended, true);
            }

            case UnclearSongAction:
                return ("I couldn't tell which song you want - try \"play <song name>\", or use `/play`.", false);

            default:
                return ("I'm not sure how to do that yet.", false);
        }
    }

    /// <summary>The server feature an action belongs to, or null for ones that always work (ending your own poll,
    /// like cancelling a post, is allowed even after polls are switched off).</summary>
    private static ServerFeature? FeatureFor(ChatAction action) => action switch
    {
        PlayMusicAction or MusicControlAction or UnclearMusicAction or UnclearSongAction => ServerFeature.Music,
        CreateLfgAction => ServerFeature.Lfg,
        SetReminderAction => ServerFeature.Reminders,
        CreatePollAction or UnclearPollAction => ServerFeature.Polls,
        WebSearchAction => ServerFeature.WebSearch,
        _ => null,
    };

    /// <summary>Controls the model can't trigger on its own: they can't be undone, and its guesses at which control
    /// was meant are sometimes wrong. The person is asked to say it plainly instead.</summary>
    private static readonly Dictionary<MusicCommand, string> PlainWordsNeeded = new()
    {
        [MusicCommand.Stop] = "stop the music",
        [MusicCommand.ClearQueue] = "clear the queue",
        [MusicCommand.RemoveMine] = "remove my songs",
        // Looping the queue stays on until someone turns it off, so a wrong guess would repeat songs for days.
        [MusicCommand.LoopSong] = "loop this song",
        [MusicCommand.LoopQueue] = "loop the queue",
    };

    /// <summary>
    /// Runs a music control as the person who asked, through the same code as the slash commands and the music menu,
    /// so the same rules apply (be in the bot's voice channel to control it, skips are votes, only your own songs are
    /// yours to remove).
    /// </summary>
    /// <param name="spoken">Said in voice: looping isn't turned on that way (see below).</param>
    private async Task<MusicReply> RunMusicControlAsync(ChatAction action, SocketGuildUser asker,
        SocketTextChannel channel, bool spoken)
    {
        if (action is not MusicControlAction { Request: var request } control)
        {
            return MusicReply.Fail("I couldn't tell what you want me to do with the music. Try \"skip\", \"pause\", " +
                                   "\"show the queue\" or \"start the music\" - or see `/help`.");
        }

        // In voice, "repeat that" / "repeat everything" is far more often "say it again" than looping, and the bot
        // only listens while no music plays, so a loop turned on there would just wait, unnoticed, to repeat songs
        // later. Turning looping off still works.
        if (spoken && request.Command is (MusicCommand.LoopSong or MusicCommand.LoopQueue))
        {
            return MusicReply.Fail("I don't turn looping on by voice, since \"repeat that\" sounds just like it. " +
                                   "Use `/loop` or the music menu.");
        }

        // A skip from a member is just one vote, but a moderator's skips go through outright, so the model's guess
        // shouldn't make one for them either.
        var phrase = request.Command == MusicCommand.Skip && Moderators.IsModerator(asker)
            ? "skip this song"
            : PlainWordsNeeded.GetValueOrDefault(request.Command);
        if (control.FromModel && phrase is not null)
        {
            return MusicReply.Fail($"Just to be sure - did you want me to **{phrase}**? Say \"@{channel.Guild.CurrentUser.DisplayName} " +
                                   $"{phrase}\" and I'll do it.");
        }

        var guild = channel.Guild;
        switch (request.Command)
        {
            case MusicCommand.Skip:
                return await _musicControls.SkipAsync(asker);
            case MusicCommand.Pause:
                return await _musicControls.PauseAsync(asker);
            case MusicCommand.Resume:
                return await _musicControls.ResumeAsync(asker);
            case MusicCommand.Stop:
                return await _musicControls.StopAsync(asker);
            case MusicCommand.NowPlaying:
                return _musicControls.NowPlaying(asker);
            case MusicCommand.ShowQueue:
                return await _musicControls.QueueAsync(asker);
            case MusicCommand.LoopSong:
                return await _musicControls.LoopAsync(asker, LoopMode.Song);
            case MusicCommand.LoopQueue:
                return await _musicControls.LoopAsync(asker, LoopMode.Queue);
            case MusicCommand.LoopOff:
                return await _musicControls.LoopAsync(asker, LoopMode.Off);
            case MusicCommand.Remove:
                return request.Title is { } title
                    ? await _musicControls.RemoveTitledAsync(asker, title)
                    : await _musicControls.RemoveAsync(asker, request.Position ?? 0);
            case MusicCommand.RemoveMine:
                return await _musicControls.RemoveMineAsync(asker);
            case MusicCommand.ClearQueue:
                return await _musicControls.ClearQueueAsync(asker);

            case MusicCommand.Start:
            {
                // "Now playing" goes where music does, wherever the request was made.
                var music = await _serverSettings.MusicChannelAsync(guild);
                return music.Or(channel) is { } musicChannel
                    ? await _musicControls.StartAsync(asker, musicChannel)
                    : MusicReply.Fail(music.Missing("Music goes"));
            }

            case MusicCommand.ShowMenu:
                return await _musicQueue.MenuLinkAsync(guild.Id) is { } link
                    ? MusicReply.Ok($"🎶 The music menu's [here]({link}) - it's pinned in the music channel.")
                    : MusicReply.Fail("There's no music menu yet - a server admin can post one with `/music-menu` " +
                                      "in the music channel. Meanwhile, ask me to play or add songs, or use `/queue`.");

            default:
                return MusicReply.Fail("I'm not sure how to do that yet.");
        }
    }

    /// <summary>Records the question if the person is allowed to ask now; true if they have to wait.</summary>
    private bool IsOnCooldown(ulong guildId, ulong userId) => !_questions.TryClaim(guildId, userId, out _);

    /// <summary>Who the bot is, then the server's personality (or the default), then the ground rules.</summary>
    private async Task<string> BuildSystemPromptAsync(SocketGuild guild, SocketGuildUser bot)
    {
        return await _personality.IntroAsync(guild.Id, bot.DisplayName, guild.Name) + GroundRules;
    }

    /// <summary>
    /// The prompt: the recent messages as a transcript ("Name: text", oldest first), then the question, marked as the
    /// one to answer. Also returns every name in it, for <see cref="RemoveScriptedLines"/>.
    /// </summary>
    /// <remarks>One message rather than a turn per Discord message: given separate "Name: text" turns, small models
    /// tend to continue the pattern and script a whole exchange between the people in the channel.</remarks>
    private async Task<(string Prompt, HashSet<string> Speakers)> BuildPromptAsync(SocketTextChannel channel,
        IUserMessage? message, IUser askedBy, SocketGuild guild, SocketGuildUser bot, string question)
    {
        var lines = new List<string>();
        var included = new HashSet<ulong>();
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bot.DisplayName };

        var historyCount = Math.Clamp(_options.HistoryMessages, 0, MaxHistoryMessages);
        if (historyCount > 0 && bot.GetPermissions(channel).ReadMessageHistory)
        {
            try
            {
                // Before the question, or (a request answered aloud, which was never posted) the latest.
                var earlier = await (message is null
                    ? channel.GetMessagesAsync(historyCount)
                    : channel.GetMessagesAsync(message, Direction.Before, historyCount)).FlattenAsync();
                foreach (var m in earlier.OrderBy(m => m.Timestamp))
                {
                    if (ToLine(m, guild, bot, speakers) is { } line)
                    {
                        lines.Add(line);
                        included.Add(m.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                // Context is a nice-to-have: answer the question on its own.
                _logger.LogDebug(ex, "Couldn't read chat history in channel {ChannelId}.", channel.Id);
            }
        }

        // Replying to an older bot message: make sure the model sees what it's being asked about.
        if (message?.ReferencedMessage is { } referenced && !included.Contains(referenced.Id) &&
            ToLine(referenced, guild, bot, speakers) is { } referencedLine)
        {
            lines.Add(referencedLine);
        }

        var asker = DisplayName(askedBy, guild);
        speakers.Add(asker);

        var prompt = lines.Count == 0
            ? $"{asker} is asking you:\n{question}"
            : "Recent messages in the channel, oldest first, for context:\n" + string.Join("\n", lines) +
              $"\n\n{asker} is asking you:\n{question}";
        return (prompt, speakers);
    }

    /// <summary>A transcript line for one earlier message, or null for ones that aren't conversation.</summary>
    private static string? ToLine(IMessage message, SocketGuild guild, SocketGuildUser bot, HashSet<string> speakers)
    {
        if (message.Author.Id == bot.Id)
        {
            // Its posts of what someone said in voice are that person talking, not the bot.
            if (ReadHeardText(message.Content) is var (speakerId, said))
            {
                var speaker = guild.GetUser(speakerId)?.DisplayName ?? "someone";
                speakers.Add(speaker);
                return $"{speaker} (in voice): {said}";
            }

            // The bot's embeds (LFG cards, music announcements) have no text and aren't conversation.
            return message.Content.Length == 0 ? null : $"{bot.DisplayName} (you): {message.Content}";
        }

        if (message.Author.IsBot || message.Author.IsWebhook)
        {
            return null;
        }

        var text = Describe(message, guild, bot.Id);
        if (text.Length == 0)
        {
            return null;
        }

        var name = DisplayName(message.Author, guild);
        speakers.Add(name);
        return $"{name}: {text}";
    }

    /// <summary>
    /// Safety net for a model that still writes the conversation as a script: drops a leading "Name:" (its own or
    /// someone's), and cuts the reply off where a line starts speaking as one of the people in the transcript.
    /// </summary>
    private static string RemoveScriptedLines(string reply, HashSet<string> speakers)
    {
        bool StartsAsSpeaker(string line, out int prefixLength)
        {
            foreach (var name in speakers)
            {
                foreach (var prefix in new[] { $"{name}:", $"{name} (you):" })
                {
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixLength = prefix.Length;
                        return true;
                    }
                }
            }

            prefixLength = 0;
            return false;
        }

        var lines = reply.Trim().Split('\n').ToList();
        if (lines.Count > 0 && StartsAsSpeaker(lines[0].TrimStart(), out var length))
        {
            lines[0] = lines[0].TrimStart()[length..].TrimStart();
        }

        var cut = lines.FindIndex(1, line => StartsAsSpeaker(line.TrimStart(), out _));
        if (cut > 0)
        {
            lines = lines.Take(cut).ToList();
        }

        var cleaned = string.Join("\n", lines).Trim();
        return cleaned.Length > 0 ? cleaned : reply.Trim();
    }

    /// <summary>The message as readable text: mentions turned into names (the bot's own mention dropped), custom
    /// emoji into :name:, and a note for attachments (unless <paramref name="noteAttachments"/> is false: the action
    /// parser gets the words alone, or the note would end up in a poll answer).</summary>
    private static string Describe(IMessage message, SocketGuild guild, ulong botId, bool noteAttachments = true)
    {
        var text = UserMentionRegex().Replace(message.Content, m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            return id == botId ? string.Empty : "@" + (guild.GetUser(id)?.DisplayName ?? "someone");
        });
        text = RoleMentionRegex().Replace(text, m => "@" + (guild.GetRole(ulong.Parse(m.Groups[1].Value))?.Name ?? "role"));
        text = ChannelMentionRegex().Replace(text, m => "#" + (guild.GetChannel(ulong.Parse(m.Groups[1].Value))?.Name ?? "channel"));
        text = CustomEmojiRegex().Replace(text, m => $":{m.Groups[1].Value}:").Trim();

        if (noteAttachments && message.Attachments.Count > 0)
        {
            text = $"{text} [{message.Attachments.Count} attachment(s)]".Trim();
        }

        return text;
    }

    private static string DisplayName(IUser user, SocketGuild guild)
        => guild.GetUser(user.Id)?.DisplayName ?? user.GlobalName ?? user.Username;

    /// <summary>
    /// Answers <paramref name="request"/>, and notes the answer on it (<see cref="ChatRequest.Answer"/>), so a spoken
    /// request can be answered aloud with exactly what was posted. Answered aloud, only an embed is posted (on its own,
    /// with <paramref name="text"/> above it); the rest is just said.
    /// </summary>
    /// <param name="spoken">What to say, when it isn't <paramref name="text"/> (or the embed's text, without any).</param>
    private async Task ReplyAsync(ChatRequest request, string text, bool suppressEmbeds = false, Embed? embed = null,
        string? spoken = null)
    {
        request.Answer = spoken ?? (text.Length > 0 ? text : embed?.Description);
        if (request.Aloud && embed is null)
        {
            return;
        }

        try
        {
            // Only the asker can be pinged, however the model's text is worded. (Suppressing embeds would hide the
            // queue/now-playing card too, so it's only done on text-only replies.)
            var flags = suppressEmbeds && embed is null ? MessageFlags.SuppressEmbeds : MessageFlags.None;
            if (request.Message is { } message)
            {
                await message.ReplyAsync(text.Length == 0 ? null : text, embed: embed,
                    allowedMentions: new AllowedMentions(AllowedMentionTypes.None) { MentionRepliedUser = true },
                    flags: flags);
            }
            else
            {
                await request.Channel.SendMessageAsync(text.Length == 0 ? null : text, embed: embed,
                    allowedMentions: AllowedMentions.None, flags: flags);
            }

            request.AnswerInChat = true;
        }
        catch (Exception ex)
        {
            // Most likely the question was deleted in the meantime.
            _logger.LogDebug(ex, "Couldn't send a chat reply in channel {ChannelId}.", request.Channel.Id);
        }
    }

    private async Task ReactAsync(IUserMessage message, string emoji)
    {
        try
        {
            await message.AddReactionAsync(new Emoji(emoji));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't react to message {MessageId}.", message.Id);
        }
    }

    /// <summary>The bot's post of a voice request: "🎙️ @name: what they said" (escaped, so it shows as said). Read back
    /// by <see cref="ReadHeardText"/> when it turns up in the channel history.</summary>
    public static string HeardText(IUser speaker, string said) => $"🎙️ {speaker.Mention}: {TextTools.Escape(said)}";

    /// <summary>The line over a web search asked for in voice and answered aloud: "🎙️ @name searched: the words",
    /// like Discord's "used /search" over the slash command's answer.</summary>
    public static string SearchedText(IUser speaker, string query)
        => $"🎙️ {speaker.Mention} searched: {TextTools.Escape(TextTools.Truncate(query, 200))}";

    /// <summary>Who spoke and what they said, from a <see cref="HeardText"/> post; null for any other message.</summary>
    public static (ulong SpeakerId, string Said)? ReadHeardText(string content)
        => HeardRegex().Match(content) is { Success: true } heard
            ? (ulong.Parse(heard.Groups["id"].Value), UnescapeRegex().Replace(heard.Groups["said"].Value, "$1"))
            : null;

    [GeneratedRegex(@"^🎙️ <@!?(?<id>\d+)>: (?<said>.+)$", RegexOptions.Singleline)]
    private static partial Regex HeardRegex();

    /// <summary>Undoes <see cref="TextTools.Escape"/>: "\*" back to "*".</summary>
    [GeneratedRegex(@"\\(.)")]
    private static partial Regex UnescapeRegex();

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMentionRegex();

    [GeneratedRegex(@"<@&(\d+)>")]
    private static partial Regex RoleMentionRegex();

    [GeneratedRegex(@"<#(\d+)>")]
    private static partial Regex ChannelMentionRegex();

    [GeneratedRegex(@"<a?:(\w+):\d+>")]
    private static partial Regex CustomEmojiRegex();
}
