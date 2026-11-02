using Discord;

namespace ModBot.Services.Reminders;

/// <summary>Whether someone can post in a channel: used both when a reminder is set and when it's delivered.</summary>
public static class ReminderPermissions
{
    /// <summary>True if <paramref name="user"/> can see <paramref name="channel"/> and send there (in a thread, send
    /// in threads). A member who is timed out can't post anywhere.</summary>
    public static bool CanPost(IGuildUser user, IGuildChannel channel)
    {
        if (user.TimedOutUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            return false;
        }

        var permissions = user.GetPermissions(channel);
        var canSend = channel is IThreadChannel ? permissions.SendMessagesInThreads : permissions.SendMessages;
        return permissions.ViewChannel && canSend;
    }
}
