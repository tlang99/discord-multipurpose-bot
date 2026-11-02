using ModBot.Models;

namespace ModBot.Services.Reminders;

/// <summary>Where a reminder's owner stands when it comes due.</summary>
public enum ReminderRecipient
{
    /// <summary>Still in the server and able to post in the reminder's channel (and so is the bot).</summary>
    CanPostInChannel,

    /// <summary>Still in the server, but the channel is gone or one of them can't post there any more (lost access,
    /// timed out). Goes to their DMs instead.</summary>
    CantPostInChannel,

    /// <summary>Left or was banned, or the bot isn't in the server any more. The reminder is dropped.</summary>
    NotInServer,

    /// <summary>Couldn't check right now (e.g. Discord didn't answer). Counts as a failed attempt.</summary>
    Unknown,
}

/// <summary>
/// Posts a reminder on Discord. Kept small so <see cref="ReminderDeliverer"/>'s rules (retries, give-up, claims)
/// can be tested without a Discord connection.
/// </summary>
public interface IReminderSender
{
    /// <summary>Checks the reminder's owner is still in the server and may post in its channel. Run just before
    /// sending, since a lot can change between setting a reminder and its delivery.</summary>
    Task<ReminderRecipient> CheckRecipientAsync(Reminder reminder);

    /// <summary>Posts it in the channel it was set in; false if the channel is gone or the post fails.</summary>
    Task<bool> TrySendInChannelAsync(Reminder reminder, string lateNote);

    /// <summary>DMs it to the person instead; false if they can't be found or their DMs are closed.</summary>
    Task<bool> TrySendDmAsync(Reminder reminder, string lateNote);
}
