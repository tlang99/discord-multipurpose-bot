using ModBot.Models;

namespace ModBot.Services.Reminders;

/// <summary>
/// Posts a reminder on Discord. Kept small so <see cref="ReminderDeliverer"/>'s rules (retries, give-up, claims)
/// can be tested without a Discord connection.
/// </summary>
public interface IReminderSender
{
    /// <summary>Posts it in the channel it was set in; false if the channel is gone or the post fails.</summary>
    Task<bool> TrySendInChannelAsync(Reminder reminder, string lateNote);

    /// <summary>DMs it to the person instead; false if they can't be found or their DMs are closed.</summary>
    Task<bool> TrySendDmAsync(Reminder reminder, string lateNote);
}
