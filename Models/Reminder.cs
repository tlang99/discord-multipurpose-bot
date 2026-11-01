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
}
