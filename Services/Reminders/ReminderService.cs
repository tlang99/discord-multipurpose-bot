using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.Services.Reminders;

/// <summary>What happened when setting or cancelling a reminder, with a message for the person.</summary>
public record ReminderResult(bool Success, string Message, Reminder? Reminder = null);

/// <summary>
/// Setting, listing and cancelling reminders, shared by /remind and the chatbot ("remind me in 20 minutes to check
/// the oven") so both follow the same rules: a time the parser understands, between a minute and
/// <see cref="ReminderOptions.MaxDays"/> away, the server's word filter, and a cap per person. Delivery is
/// ReminderDeliveryService's job.
/// </summary>
public class ReminderService
{
    public const int MaxTextLength = 500;
    private const string IdAlphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/o, 1/l/i, easy to type

    private readonly IReminderStore _store;
    private readonly IGuildSettingsStore _settingsStore;
    private readonly ReminderOptions _options;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(IReminderStore store, IGuildSettingsStore settingsStore, IOptions<ReminderOptions> options,
        ILogger<ReminderService> logger)
    {
        _store = store;
        _settingsStore = settingsStore;
        _options = options.Value;
        _logger = logger;
        TimeZone = ResolveTimeZone(_options.TimeZone, logger);

        if (_options.MaxPerUser < 1 || _options.MaxDays < 1)
        {
            logger.LogWarning("Reminders:MaxPerUser ({MaxPerUser}) and Reminders:MaxDays ({MaxDays}) must be at least 1; " +
                              "using 1 for any that aren't.", _options.MaxPerUser, _options.MaxDays);
        }
    }

    /// <summary>The time zone clock times ("at 5pm") are read in.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Sets a reminder from a "when" like "in 2 hours" or "tomorrow 9am" (as typed into /remind).</summary>
    /// <param name="applyWordFilter">Whether the server's word filter applies to this person (auto-mod exempts
    /// moderators).</param>
    public async Task<ReminderResult> CreateAsync(ulong guildId, ulong channelId, ulong userId, string when, string text,
        bool applyWordFilter, ulong? sourceMessageId = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (ReminderTimeParser.Find(when, now, TimeZone) is not { } time)
        {
            return new(false, $"I couldn't tell when `{when.Trim()}` is. Try something like `10m`, `2h30m`, " +
                              "`in 3 days`, `at 5pm` or `tomorrow 9am`.");
        }

        return await CreateAtAsync(guildId, channelId, userId, time.DueAt, text, applyWordFilter, sourceMessageId);
    }

    /// <summary>Sets a reminder for a time already worked out (the chatbot reads it from the message).</summary>
    public async Task<ReminderResult> CreateAtAsync(ulong guildId, ulong channelId, ulong userId, DateTimeOffset dueAt,
        string text, bool applyWordFilter, ulong? sourceMessageId = null)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return new(false, "What should I remind you about?");
        }

        if (text.Length > MaxTextLength)
        {
            return new(false, $"Keep reminders to {MaxTextLength} characters.");
        }

        var now = DateTimeOffset.UtcNow;
        if (dueAt < now.AddSeconds(55))
        {
            return new(false, "Reminders need to be at least a minute away.");
        }

        if (dueAt > now.AddDays(MaxDays))
        {
            return new(false, $"Reminders can be at most {MaxDays} days away.");
        }

        // The text is posted publicly later, so it follows the server's word rules now, while the person can fix it.
        if (applyWordFilter)
        {
            var settings = await _settingsStore.GetAsync(guildId);
            if (settings.AutoModEnabled && TextFilter.FindViolation(text, settings) is not null)
            {
                return new(false, "That reminder breaks this server's word filter, so I can't post it later.");
            }
        }

        var reminder = new Reminder
        {
            GuildId = guildId,
            ChannelId = channelId,
            UserId = userId,
            SourceMessageId = sourceMessageId,
            Text = text,
            DueAt = dueAt,
            CreatedAt = now,
        };

        // The cap and the ID are checked in the same step as the add, so two requests at once can't both get in over
        // the limit, or share an ID and overwrite each other. A taken ID just means trying another.
        ReminderAddResult added;
        do
        {
            reminder.Id = NewId();
            added = await _store.TryAddAsync(reminder, MaxPerUser);
        }
        while (added == ReminderAddResult.IdTaken);

        if (added == ReminderAddResult.LimitReached)
        {
            return new(false, $"You already have {MaxPerUser} reminders here, the most allowed. Cancel one with " +
                              "`/reminders cancel` first.");
        }

        return new(true, $"⏰ Got it - I'll remind you {Relative(dueAt)} ({Absolute(dueAt)}): {text}\n" +
                         $"-# Cancel it with `/reminders cancel id:{reminder.Id}`.", reminder);
    }

    public Task<IReadOnlyList<Reminder>> ListAsync(ulong guildId, ulong userId) => _store.GetForUserAsync(guildId, userId);

    /// <summary>Cancels one of the user's own reminders by its code.</summary>
    public async Task<ReminderResult> CancelAsync(ulong guildId, ulong userId, string id)
    {
        id = id.Trim().ToLowerInvariant();

        // Checked in one step with the delivery's claim, so "cancelled" is never said about a reminder mid-send.
        var (result, reminder) = await _store.TryCancelAsync(id, guildId, userId, DateTimeOffset.UtcNow);
        return result switch
        {
            ReminderCancelResult.Cancelled => new(true, $"Cancelled `{id}`: {reminder!.Text}", reminder),
            ReminderCancelResult.InFlight => new(false, $"`{id}` is being delivered right now, so it's too late to cancel."),
            _ => new(false, $"You don't have a reminder `{id}` here - check `/reminders list`."),
        };
    }

    /// <summary>Discord timestamp markup: shown to each viewer in their own time zone.</summary>
    public static string Relative(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:R>";

    public static string Absolute(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:f>";

    private static string NewId()
        => string.Create(4, 0, (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
            }
        });

    private int MaxPerUser => Math.Max(1, _options.MaxPerUser);

    private int MaxDays => Math.Max(1, _options.MaxDays);

    private static TimeZoneInfo ResolveTimeZone(string name, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Reminders:TimeZone '{TimeZone}' isn't a known time zone; using this machine's ({Local}).",
                name, TimeZoneInfo.Local.Id);
            return TimeZoneInfo.Local;
        }
    }
}
