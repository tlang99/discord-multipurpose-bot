namespace ModBot.Models;

/// <summary>A pending reminder. Delivered reminders are deleted.</summary>
public class Reminder
{
    /// <summary>Short code shown in /reminders list and used by /reminders cancel (e.g. "k3x9").</summary>
    public string Id { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    /// <summary>Where it's delivered: the channel it was set in.</summary>
    public ulong ChannelId { get; set; }

    public ulong UserId { get; set; }

    /// <summary>The chat message that asked for it, which the delivery replies to. Null for /remind.</summary>
    public ulong? SourceMessageId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Failed delivery attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>After a failed delivery, when to try again (backing off). Null until the first failure.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Set while a delivery is sending it, so a cancel can't report success for a reminder that's about to
    /// arrive anyway. It expires on its own, so a crash mid-send just means a retry.</summary>
    public DateTimeOffset? ClaimedUntil { get; set; }
}
