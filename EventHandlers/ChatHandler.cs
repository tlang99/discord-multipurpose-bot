using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Modules.Lfg;
using ModBot.Modules.Music;
using ModBot.Modules.Polls;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
using ModBot.Services.Polls;
using ModBot.Services.Reminders;
using ModBot.Services.Storage;

namespace ModBot.EventHandlers;

/// <summary>
/// The chatbot: answers messages that @mention the bot or reply to one of its messages, using the recent messages in
/// the channel as context. Questions and answers are both checked against the server's word filter, replies never
/// ping anyone but the asker, and each person has a cooldown so one user can't run up usage.
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
        "poll: what should we play? ranked, casual or zombies\" or \"end my poll\") or see /help. How the " +
        "music works: there's one standing queue anyone can add to any time, even when nothing's playing; songs play " +
        "in turns, one from each person; skipping a song takes votes from more than half the listeners, unless you " +
        "picked it; and there's a pinned music menu with buttons in the music channel.";

    private readonly IChatService _chat;
    private readonly ChatActionParser _actionParser;
    private readonly MusicService _music;
    private readonly MusicControls _musicControls;
    private readonly MusicQueueService _musicQueue;
    private readonly LfgPoster _lfgPoster;
    private readonly ReminderService _reminders;
    private readonly PollPoster _polls;
    private readonly IGuildSettingsStore _settingsStore;
    private readonly ChatPersonality _personality;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatHandler> _logger;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _lastQuestion = new();

    public ChatHandler(IChatService chat, ChatActionParser actionParser, MusicService music,
        MusicControls musicControls, MusicQueueService musicQueue, LfgPoster lfgPoster,
        ReminderService reminders, PollPoster polls,
        IGuildSettingsStore settingsStore, ChatPersonality personality, IOptions<ChatOptions> options,
        ILogger<ChatHandler> logger)
    {
        _chat = chat;
        _actionParser = actionParser;
        _music = music;
        _musicControls = musicControls;
        _musicQueue = musicQueue;
        _lfgPoster = lfgPoster;
        _reminders = reminders;
        _polls = polls;
        _settingsStore = settingsStore;
        _personality = personality;
        _options = options.Value;
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

        if (!ChannelAccess.CanPost(bot, channel))
        {
            return;
        }

        // Auto-mod is about to remove a question that breaks the word filter; don't answer it.
        var settings = await _settingsStore.GetAsync(guild.Id);
        if (TextFilter.AppliesTo(settings, message.Author as SocketGuildUser) &&
            TextFilter.FindViolation(message.Content, settings) is not null)
        {
            return;
        }

        var question = Describe(message, guild, bot.Id);
        if (question.Length == 0)
        {
            await ReplyAsync(message, $"👋 Ask me something after the mention, like \"@{bot.DisplayName} what's a " +
                                      "good Hardpoint strategy?\"");
            return;
        }

        if (IsOnCooldown(guild.Id, message.Author.Id))
        {
            await ReactAsync(message, "⏳");
            return;
        }

        string? reply = null;
        ChatAction? action = null;
        using (channel.EnterTypingState())
        {
            // How many requests reach the model at once is limited inside the chat service, shared with news.
            try
            {
                // "Play X" / "make an LFG" are done, not chatted about. Only the asker's own message is looked at.
                if (_options.Actions && message.Author is SocketGuildUser &&
                    Describe(message, guild, bot.Id, noteAttachments: false) is { Length: > 0 } request)
                {
                    action = await _actionParser.ParseAsync(request);
                }

                if (action is null)
                {
                    var (prompt, speakers) = await BuildPromptAsync(channel, message, guild, bot, question);
                    reply = await _chat.ReplyAsync(await BuildSystemPromptAsync(guild, bot),
                        [new ChatTurn(ChatRole.User, prompt)]);
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
                try
                {
                    if (action is MusicControlAction or UnclearMusicAction)
                    {
                        var music = await RunMusicControlAsync(action, (SocketGuildUser)message.Author, channel);
                        (text, suppressEmbeds, embed) = (music.Text ?? "", music.SuppressEmbeds, music.Embed);
                    }
                    else
                    {
                        (text, suppressEmbeds) = await RunActionAsync(action, (SocketGuildUser)message.Author, channel,
                            message);
                    }
                }
                catch (Exception ex)
                {
                    // Lavalink or Discord failing mid-action: say so instead of leaving the request unanswered.
                    _logger.LogWarning(ex, "Chat action {Action} failed in guild {GuildId}.", action.GetType().Name, guild.Id);
                    text = "Something went wrong doing that - try again in a bit, or use the slash command.";
                }

                // A posted poll is its own answer (it's a reply to the request); every other action replies.
                if (action is CreatePollAction && text.Length == 0)
                {
                    return;
                }

                // Song and playlist titles are third-party text, so action replies follow the word rules too.
                if (settings.AutoModEnabled &&
                    TextFilter.FindViolation(text + "\n" + EmbedText(embed), settings) is not null)
                {
                    text = action is PlayMusicAction or MusicControlAction
                        ? "My reply named a song whose title breaks this server's word filter, so I won't repeat it " +
                          "here. Check `/queue` to see what happened."
                        : "My reply broke this server's word filter, so I'm keeping the details to myself.";
                    suppressEmbeds = false;
                    embed = null;
                }

                await ReplyAsync(message, text, suppressEmbeds, embed);
                return;
            }
        }

        reply ??= "I couldn't come up with an answer right now - try again in a bit.";

        reply = TextTools.Truncate(reply, DiscordMessageLimit);

        // The model can be talked into saying anything, so its answers follow the same word rules as members.
        if (settings.AutoModEnabled && TextFilter.FindViolation(reply, settings) is not null)
        {
            reply = "I came up with an answer, but it broke this server's word filter, so I'm keeping it to myself. " +
                    "Try asking another way.";
        }

        await ReplyAsync(message, reply);
    }

    /// <summary>
    /// Does what the message asked, as the person who asked, through the same code as /play, /lfg and /remind (so the
    /// same rules apply: in a voice channel to play, one open LFG each, the reminder limits). Music and LFG posts go to
    /// their own channels wherever the request was made; reminders are delivered here, replying to the request.
    /// Polls are posted right here, as a reply to the request. Returns the reply (empty when the action already
    /// answered), and whether to hide link previews in it.
    /// </summary>
    private async Task<(string Text, bool SuppressEmbeds)> RunActionAsync(ChatAction action, SocketGuildUser asker,
        SocketTextChannel channel, SocketUserMessage request)
    {
        var guild = channel.Guild;
        var requestMessageId = request.Id;
        switch (action)
        {
            case PlayMusicAction play:
            {
                if (MusicChannel(channel) is not { } musicChannel)
                {
                    return (ConfiguredChannels.Missing("Music goes", _music.ChannelName), false);
                }

                // Into the standing queue, in the person's turns. "Play" also starts the music if it isn't playing.
                var result = play.StartPlaying
                    ? await _musicControls.PlayAsync(asker, musicChannel, play.Query)
                    : await _musicControls.AddAsync(asker, musicChannel, play.Query);
                var where = result.Success && musicChannel.Id != channel.Id ? $" (music's in {musicChannel.Mention})" : "";
                return ((result.Text ?? "Done.") + where, result.SuppressEmbeds);
            }

            case CreateLfgAction lfg:
            {
                if (ConfiguredChannels.FindOr(guild, _lfgPoster.ChannelName, channel) is not { } lfgChannel)
                {
                    return (ConfiguredChannels.Missing("LFG posts go", _lfgPoster.ChannelName), false);
                }

                // Posting for someone is only allowed where they could post themselves.
                if (!ChannelAccess.CanPost(asker, lfgChannel))
                {
                    return ($"You can't post in {lfgChannel.Mention}, so I can't post an LFG there for you.", false);
                }

                // No note from chat: free text on a public card is left to /lfg.
                var result = await _lfgPoster.PostAsync(asker, lfgChannel, lfg.Players, lfg.Details, note: null);
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
                return (await _polls.PostReplyAsync(asker, channel, draft, request, poll.FromModel) ?? "", false);
            }

            case UnclearPollAction:
                return ("I couldn't tell what the poll should ask, or what the choices are. Try \"make a poll: what " +
                        "should we play? ranked, casual or zombies\", or use `/poll`.", false);

            case EndPollAction:
                return (await _polls.EndRequestedAsync(asker, channel, request.ReferencedMessage?.Id), true);

            case UnclearSongAction:
                return ("I couldn't tell which song you want - try \"play <song name>\", or use `/play`.", false);

            default:
                return ("I'm not sure how to do that yet.", false);
        }
    }

    /// <summary>Controls the model can't trigger on its own: they can't be undone, and its guesses at which control
    /// was meant are sometimes wrong. The person is asked to say it plainly instead.</summary>
    private static readonly Dictionary<MusicCommand, string> PlainWordsNeeded = new()
    {
        [MusicCommand.Stop] = "stop the music",
        [MusicCommand.ClearQueue] = "clear the queue",
        [MusicCommand.RemoveMine] = "remove my songs",
    };

    /// <summary>
    /// Runs a music control as the person who asked, through the same code as the slash commands and the music menu,
    /// so the same rules apply (be in the bot's voice channel to control it, skips are votes, only your own songs are
    /// yours to remove).
    /// </summary>
    private async Task<MusicReply> RunMusicControlAsync(ChatAction action, SocketGuildUser asker, SocketTextChannel channel)
    {
        if (action is not MusicControlAction { Request: var request } control)
        {
            return MusicReply.Fail("I couldn't tell what you want me to do with the music. Try \"skip\", \"pause\", " +
                                   "\"show the queue\" or \"start the music\" - or see `/help`.");
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
                return await _musicControls.RemoveAsync(asker, request.Position ?? 0);
            case MusicCommand.RemoveMine:
                return await _musicControls.RemoveMineAsync(asker);
            case MusicCommand.ClearQueue:
                return await _musicControls.ClearQueueAsync(asker);

            case MusicCommand.Start:
            {
                // "Now playing" goes where music does, wherever the request was made.
                return MusicChannel(channel) is { } musicChannel
                    ? await _musicControls.StartAsync(asker, musicChannel)
                    : MusicReply.Fail(ConfiguredChannels.Missing("Music goes", _music.ChannelName));
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

    /// <summary>Where music goes for a request made in <paramref name="asked"/>: the music channel, or right there if
    /// music isn't limited to one. Null if the server doesn't have the music channel.</summary>
    private ITextChannel? MusicChannel(SocketTextChannel asked)
        => ConfiguredChannels.FindOr(asked.Guild, _music.ChannelName, asked);

    /// <summary>All the text in an embed, for the word filter.</summary>
    private static string EmbedText(Embed? embed)
        => embed is null
            ? ""
            : string.Join("\n", new[] { embed.Title, embed.Description, embed.Footer?.Text }
                .Concat(embed.Fields.SelectMany(f => new[] { f.Name, f.Value }))
                .Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Records the question if the person is allowed to ask now; true if they have to wait.</summary>
    private bool IsOnCooldown(ulong guildId, ulong userId)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _options.CooldownSeconds));
        var allowed = false;
        _lastQuestion.AddOrUpdate((guildId, userId),
            _ =>
            {
                allowed = true;
                return now;
            },
            (_, last) =>
            {
                allowed = now - last >= cooldown;
                return allowed ? now : last;
            });
        return !allowed;
    }

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
        SocketUserMessage message, SocketGuild guild, SocketGuildUser bot, string question)
    {
        var lines = new List<string>();
        var included = new HashSet<ulong>();
        var speakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bot.DisplayName };

        var historyCount = Math.Clamp(_options.HistoryMessages, 0, MaxHistoryMessages);
        if (historyCount > 0 && bot.GetPermissions(channel).ReadMessageHistory)
        {
            try
            {
                var earlier = await channel.GetMessagesAsync(message, Direction.Before, historyCount).FlattenAsync();
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
        if (message.ReferencedMessage is { } referenced && !included.Contains(referenced.Id) &&
            ToLine(referenced, guild, bot, speakers) is { } referencedLine)
        {
            lines.Add(referencedLine);
        }

        var asker = DisplayName(message.Author, guild);
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

    private async Task ReplyAsync(SocketUserMessage message, string text, bool suppressEmbeds = false,
        Embed? embed = null)
    {
        try
        {
            // Only the asker can be pinged, however the model's text is worded. (Suppressing embeds would hide the
            // queue/now-playing card too, so it's only done on text-only replies.)
            await message.ReplyAsync(text.Length == 0 ? null : text, embed: embed,
                allowedMentions: new AllowedMentions(AllowedMentionTypes.None) { MentionRepliedUser = true },
                flags: suppressEmbeds && embed is null ? MessageFlags.SuppressEmbeds : MessageFlags.None);
        }
        catch (Exception ex)
        {
            // Most likely the question was deleted in the meantime.
            _logger.LogDebug(ex, "Couldn't send a chat reply in channel {ChannelId}.", message.Channel.Id);
        }
    }

    private async Task ReactAsync(SocketUserMessage message, string emoji)
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

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMentionRegex();

    [GeneratedRegex(@"<@&(\d+)>")]
    private static partial Regex RoleMentionRegex();

    [GeneratedRegex(@"<#(\d+)>")]
    private static partial Regex ChannelMentionRegex();

    [GeneratedRegex(@"<a?:(\w+):\d+>")]
    private static partial Regex CustomEmojiRegex();
}
