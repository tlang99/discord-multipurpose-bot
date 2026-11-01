using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.Moderation;
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

    private const string DefaultSystemPrompt =
        "You are {botName}, the bot in the Discord server \"{serverName}\". Chat in a friendly, casual way and keep " +
        "answers short: usually a few sentences, and well under 1,500 characters unless someone asks for detail. " +
        "Messages from people start with their display name, like \"Sam: hey\". Use Discord markdown sparingly. " +
        "Never write @everyone or @here, and don't try to ping people. You can't take actions in the server; for the " +
        "bot's commands (LFG posts, music), point people to /help.";

    private readonly IChatService _chat;
    private readonly IGuildSettingsStore _settingsStore;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatHandler> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _lastQuestion = new();

    public ChatHandler(IChatService chat, IGuildSettingsStore settingsStore, IOptions<ChatOptions> options,
        ILogger<ChatHandler> logger)
    {
        _chat = chat;
        _settingsStore = settingsStore;
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRequests));
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

        var permissions = bot.GetPermissions(channel);
        var canSend = channel is SocketThreadChannel ? permissions.SendMessagesInThreads : permissions.SendMessages;
        if (!permissions.ViewChannel || !canSend)
        {
            return;
        }

        // Auto-mod is about to remove a question that breaks the word filter; don't answer it.
        var settings = await _settingsStore.GetAsync(guild.Id);
        var filterApplies = settings.AutoModEnabled &&
                            (message.Author as SocketGuildUser)?.GuildPermissions.ManageMessages != true;
        if (filterApplies && TextFilter.FindViolation(message.Content, settings) is not null)
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

        string reply;
        using (channel.EnterTypingState())
        {
            await _concurrency.WaitAsync();
            try
            {
                var conversation = await BuildConversationAsync(channel, message, guild, bot.Id, question);
                reply = await _chat.ReplyAsync(BuildSystemPrompt(guild, bot), conversation);
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
            finally
            {
                _concurrency.Release();
            }
        }

        if (reply.Length > DiscordMessageLimit)
        {
            reply = reply[..(DiscordMessageLimit - 3)].TrimEnd() + "...";
        }

        // The model can be talked into saying anything, so its answers follow the same word rules as members.
        if (settings.AutoModEnabled && TextFilter.FindViolation(reply, settings) is not null)
        {
            reply = "I came up with an answer, but it broke this server's word filter, so I'm keeping it to myself. " +
                    "Try asking another way.";
        }

        await ReplyAsync(message, reply);
    }

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

    private string BuildSystemPrompt(SocketGuild guild, SocketGuildUser bot)
        => (string.IsNullOrWhiteSpace(_options.SystemPrompt) ? DefaultSystemPrompt : _options.SystemPrompt)
            .Replace("{botName}", bot.DisplayName)
            .Replace("{serverName}", guild.Name);

    /// <summary>The recent messages before the question (oldest first), then the question itself. People's messages
    /// are prefixed with their display name so the model can tell speakers apart.</summary>
    private async Task<List<ChatTurn>> BuildConversationAsync(SocketTextChannel channel, SocketUserMessage message,
        SocketGuild guild, ulong botId, string question)
    {
        var turns = new List<ChatTurn>();
        var included = new HashSet<ulong>();

        var historyCount = Math.Clamp(_options.HistoryMessages, 0, MaxHistoryMessages);
        if (historyCount > 0 && guild.CurrentUser.GetPermissions(channel).ReadMessageHistory)
        {
            try
            {
                var earlier = await channel.GetMessagesAsync(message, Direction.Before, historyCount).FlattenAsync();
                foreach (var m in earlier.OrderBy(m => m.Timestamp))
                {
                    if (ToTurn(m, guild, botId) is { } turn)
                    {
                        turns.Add(turn);
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
            ToTurn(referenced, guild, botId) is { } referencedTurn)
        {
            turns.Add(referencedTurn);
        }

        turns.Add(new ChatTurn(ChatRole.User, $"{DisplayName(message.Author, guild)}: {question}"));
        return turns;
    }

    private static ChatTurn? ToTurn(IMessage message, SocketGuild guild, ulong botId)
    {
        if (message.Author.Id == botId)
        {
            // The bot's embeds (LFG cards, music announcements) have no text and aren't conversation.
            return message.Content.Length == 0 ? null : new ChatTurn(ChatRole.Assistant, message.Content);
        }

        if (message.Author.IsBot || message.Author.IsWebhook)
        {
            return null;
        }

        var text = Describe(message, guild, botId);
        return text.Length == 0 ? null : new ChatTurn(ChatRole.User, $"{DisplayName(message.Author, guild)}: {text}");
    }

    /// <summary>The message as readable text: mentions turned into names (the bot's own mention dropped), custom
    /// emoji into :name:, and a note for attachments.</summary>
    private static string Describe(IMessage message, SocketGuild guild, ulong botId)
    {
        var text = UserMentionRegex().Replace(message.Content, m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            return id == botId ? string.Empty : "@" + (guild.GetUser(id)?.DisplayName ?? "someone");
        });
        text = RoleMentionRegex().Replace(text, m => "@" + (guild.GetRole(ulong.Parse(m.Groups[1].Value))?.Name ?? "role"));
        text = ChannelMentionRegex().Replace(text, m => "#" + (guild.GetChannel(ulong.Parse(m.Groups[1].Value))?.Name ?? "channel"));
        text = CustomEmojiRegex().Replace(text, m => $":{m.Groups[1].Value}:").Trim();

        if (message.Attachments.Count > 0)
        {
            text = $"{text} [{message.Attachments.Count} attachment(s)]".Trim();
        }

        return text;
    }

    private static string DisplayName(IUser user, SocketGuild guild)
        => guild.GetUser(user.Id)?.DisplayName ?? user.GlobalName ?? user.Username;

    private async Task ReplyAsync(SocketUserMessage message, string text)
    {
        try
        {
            // Only the asker can be pinged, however the model's text is worded.
            await message.ReplyAsync(text,
                allowedMentions: new AllowedMentions(AllowedMentionTypes.None) { MentionRepliedUser = true });
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
