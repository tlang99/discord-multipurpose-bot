using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Services.Moderation;
using ModBot.Services.Polls;
using ModBot.Services.Storage;

namespace ModBot.Modules.Polls;

/// <summary>
/// Posts and ends Discord's native polls, shared by /poll, the "End poll" app command and the chatbot ("make a poll:
/// ranked or casual?") so all of them follow the same rules: both the person and the bot need to be able to post
/// polls in the channel, the server's word filter, and a cooldown per person. Discord does the rest (voting, live
/// counts, closing on time and posting the result), so nothing is stored.
/// </summary>
/// <remarks>
/// The bot is the poll's author as far as Discord is concerned, so only the bot can end one early. Who asked for it is
/// written at the top of the message ("📊 Poll by @name", without pinging), and read back from there; the bot only
/// trusts that line on its own messages.
/// </remarks>
public partial class PollPoster
{
    private readonly IGuildSettingsStore _settingsStore;
    private readonly PollOptions _options;
    private readonly ILogger<PollPoster> _logger;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _lastPoll = new();

    public PollPoster(IGuildSettingsStore settingsStore, IOptions<PollOptions> options, ILogger<PollPoster> logger)
    {
        _settingsStore = settingsStore;
        _options = options.Value;
        _logger = logger;
    }

    public int DefaultHours => Math.Clamp(_options.DefaultHours, 1, PollRules.MaxHours);

    private TimeSpan Cooldown => TimeSpan.FromSeconds(Math.Max(0, _options.CooldownSeconds));

    /// <summary>
    /// Everything short of posting: permissions, the word filter and the cooldown, which it claims. Null means go
    /// ahead; call <see cref="Release"/> if posting then fails, so the person isn't held to a cooldown for nothing.
    /// </summary>
    /// <param name="writtenByModel">The chatbot's model wrote the question. Model text is always checked while
    /// auto-mod is on, as with every other reply the bot writes, even when a moderator asked.</param>
    public async Task<string?> PrepareAsync(IGuildUser asker, ITextChannel channel, PollDraft draft,
        bool writtenByModel = false)
    {
        // Slash commands work even where the person can't type, so without this anyone could have the bot post a
        // poll in a read-only channel like #rules.
        if (!ChannelAccess.CanPost(asker, channel) || !asker.GetPermissions(channel).SendPolls)
        {
            return $"You can't post polls in {channel.Mention}, so I can't post one there for you.";
        }

        var bot = await asker.Guild.GetCurrentUserAsync();
        if (!ChannelAccess.CanPost(bot, channel) || !bot.GetPermissions(channel).SendPolls)
        {
            return $"I need **Send Messages** and **Create Polls** in {channel.Mention} to post polls there.";
        }

        // Same exemption as auto-mod: moderators aren't held to the word filter, for their own words.
        var settings = await _settingsStore.GetAsync(asker.GuildId);
        if (TextFilter.AppliesTo(settings, writtenByModel ? null : asker) &&
            TextFilter.FindViolation(draft.Question + "\n" + string.Join("\n", draft.Answers), settings) is not null)
        {
            return "That poll breaks this server's word filter, so I can't post it.";
        }

        if (!TryClaimCooldown(asker.GuildId, asker.Id, out var wait))
        {
            return $"You just posted a poll - you can post another {CountdownTo(wait)}.";
        }

        return null;
    }

    public void Release(ulong guildId, ulong userId) => _lastPoll.TryRemove((guildId, userId), out _);

    /// <summary>Posts the poll as a reply to <paramref name="request"/> (the chatbot's path; /poll responds to the
    /// interaction instead). Returns null on success, or what went wrong.</summary>
    public async Task<string?> PostReplyAsync(IGuildUser asker, ITextChannel channel, PollDraft draft,
        IUserMessage request, bool writtenByModel = false)
    {
        if (await PrepareAsync(asker, channel, draft, writtenByModel) is { } problem)
        {
            return problem;
        }

        try
        {
            await channel.SendMessageAsync(Header(asker.Id), poll: ToProperties(draft),
                allowedMentions: new AllowedMentions(AllowedMentionTypes.None) { MentionRepliedUser = false },
                messageReference: new MessageReference(request.Id, channel.Id, channel.GuildId,
                    failIfNotExists: false));
            return null;
        }
        catch (Exception ex)
        {
            Release(asker.GuildId, asker.Id);
            _logger.LogWarning(ex, "Couldn't post a poll in channel {ChannelId}.", channel.Id);
            return $"Couldn't post your poll in {channel.Mention} - try again in a bit, or use `/poll`.";
        }
    }

    /// <summary>The line above the poll that says whose it is. The mention doesn't ping (allowed mentions are off) but
    /// shows their name, and <see cref="OwnerOf"/> reads it back.</summary>
    public static string Header(ulong userId) => $"📊 Poll by <@{userId}>";

    public static PollProperties ToProperties(PollDraft draft) => new()
    {
        Question = new PollMediaProperties { Text = draft.Question },
        Answers = draft.Answers.Select(a => new PollMediaProperties { Text = a }).ToList(),
        Duration = (uint)draft.Hours,
        AllowMultiselect = draft.AllowMultiple,
        LayoutType = PollLayout.Default,
    };

    /// <summary>Who asked for the bot's poll in <paramref name="message"/>, or null if it isn't one of the bot's
    /// polls.</summary>
    public static ulong? OwnerOf(IMessage message, ulong botId)
        => message.Author.Id == botId && message is IUserMessage { Poll: not null } &&
           HeaderRegex().Match(message.Content) is { Success: true } header
            ? ulong.Parse(header.Groups[1].Value)
            : null;

    /// <summary>Still taking votes: not ended early or run out.</summary>
    public static bool IsOpen(IUserMessage message)
        => message.Poll is { } poll && poll.Results?.IsFinalized != true &&
           poll.ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>Ends the bot's poll in <paramref name="message"/> early, if <paramref name="user"/> asked for it or is
    /// a moderator. Returns what happened, for the person.</summary>
    public async Task<string> EndAsync(IGuildUser user, IUserMessage message)
    {
        var bot = await user.Guild.GetCurrentUserAsync();
        if (OwnerOf(message, bot.Id) is not { } owner)
        {
            return "That isn't one of my polls.";
        }

        if (owner != user.Id && !Moderators.IsModerator(user))
        {
            return "Only whoever asked for that poll (or a moderator) can end it.";
        }

        if (!IsOpen(message))
        {
            return "That poll has already ended.";
        }

        try
        {
            await message.EndPollAsync(options: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't end poll {MessageId}.", message.Id);
            return "Couldn't end that poll - try again in a bit.";
        }

        // Discord posts the results under the poll itself.
        return $"Ended the poll. [Results]({MessageTools.Link(user.GuildId, message.Channel.Id, message.Id)})";
    }

    /// <summary>
    /// "End my poll" from chat. Sent as a reply, it means the replied-to message and nothing else: it's fetched fresh
    /// (a reply's copy of it can lack the poll), and if it isn't an open poll they may end, they're told so rather
    /// than having some other poll ended, since ending can't be undone. Without a reply, "end my poll" is their newest
    /// open poll in the channel. "End the poll" / "end that poll" means the newest open poll there, whoever's it is,
    /// and it's only ended if that one is theirs: a moderator saying "end that poll" about a member's poll shouldn't
    /// end the moderator's own instead. Anything else is asked to reply to the poll they mean.
    /// </summary>
    public async Task<string> EndRequestedAsync(IGuildUser asker, ITextChannel channel, ulong? repliedToId,
        bool saidMine)
    {
        var bot = await asker.Guild.GetCurrentUserAsync();
        var canReadHistory = bot.GetPermissions(channel).ReadMessageHistory;
        if (!canReadHistory)
        {
            return "I need **Read Message History** here to find your poll. Right-click it → Apps → End poll instead.";
        }

        if (repliedToId is { } id)
        {
            if (await channel.GetMessageAsync(id) is not IUserMessage replied || replied.Author.Id != bot.Id)
            {
                return "The message you replied to isn't one of my polls.";
            }

            // "End my poll" on someone else's poll: a moderator could end it, but they said theirs, so don't guess.
            return saidMine && OwnerOf(replied, bot.Id) is { } owner && owner != asker.Id
                ? "That poll isn't yours. Say \"end the poll\" in your reply if you mean that one."
                : await EndAsync(asker, replied);
        }

        if (saidMine)
        {
            return await FindOpenPollAsync(channel, m => OwnerOf(m, bot.Id) == asker.Id) is { } mine
                ? await EndAsync(asker, mine)
                : "I can't find an open poll of yours in the last 50 messages here. Reply to the poll with \"end " +
                  "the poll\", or right-click it → Apps → End poll.";
        }

        // "End that poll" means the newest open poll here, anyone's, including members' own Discord polls. It's only
        // ended if it's one of the bot's polls that this person asked for.
        var newest = await FindOpenPollAsync(channel, _ => true);
        if (newest is null)
        {
            return "I can't find an open poll in the last 50 messages here. Reply to the poll with \"end the poll\", " +
                   "or right-click it → Apps → End poll.";
        }

        return OwnerOf(newest, bot.Id) == asker.Id
            ? await EndAsync(asker, newest)
            : "The newest poll here isn't one of yours, so I'm not sure which one you mean. Reply to the poll with " +
              "\"end the poll\", or say \"end my poll\" for your own.";
    }

    /// <summary>The newest open poll among the last 50 messages in the channel that <paramref name="matches"/>, from
    /// any author (members can post Discord polls too).</summary>
    private static async Task<IUserMessage?> FindOpenPollAsync(ITextChannel channel, Func<IUserMessage, bool> matches)
    {
        var recent = await channel.GetMessagesAsync(50).FlattenAsync();
        return recent.OfType<IUserMessage>()
            .Where(m => IsOpen(m) && matches(m))
            .OrderByDescending(m => m.Timestamp)
            .FirstOrDefault();
    }

    private bool TryClaimCooldown(ulong guildId, ulong userId, out TimeSpan wait)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = Cooldown;
        var allowed = false;
        var last = _lastPoll.AddOrUpdate((guildId, userId),
            _ =>
            {
                allowed = true;
                return now;
            },
            (_, previous) =>
            {
                allowed = now - previous >= cooldown;
                return allowed ? now : previous;
            });
        wait = allowed ? TimeSpan.Zero : last + cooldown - now;
        return allowed;
    }

    /// <summary>"in 42 seconds", as a Discord timestamp so it counts down by itself.</summary>
    private static string CountdownTo(TimeSpan wait)
        => $"<t:{DateTimeOffset.UtcNow.Add(wait).ToUnixTimeSeconds() + 1}:R>";

    [GeneratedRegex(@"^📊 Poll by <@!?(\d+)>")]
    private static partial Regex HeaderRegex();
}
