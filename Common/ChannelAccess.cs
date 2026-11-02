using Discord;

namespace ModBot.Common;

/// <summary>The permission checks several features make before posting somewhere.</summary>
public static class ChannelAccess
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

    /// <summary>Why the bot can't post embeds in <paramref name="channel"/>, or null if it can.</summary>
    /// <param name="what">What it would post, e.g. "LFGs" or "news".</param>
    public static string? WhyBotCantPostEmbeds(IGuildUser bot, IGuildChannel channel, string what)
        => bot.GetPermissions(channel) is { ViewChannel: true, SendMessages: true, EmbedLinks: true }
            ? null
            : $"I need **View Channel**, **Send Messages**, and **Embed Links** in {MentionOf(channel)} to post " +
              $"{what} there.";

    /// <summary>
    /// Why a pinned panel (the help list, the music menu, the LFG ping panel) can't be posted in
    /// <paramref name="channel"/>, or null if it can. Manage Server alone shouldn't let someone use the bot to post and
    /// pin in a channel they couldn't post and pin in themselves, like a read-only #announcements.
    /// </summary>
    /// <param name="what">What's being posted, e.g. "the music menu".</param>
    public static string? WhyCantPostPanel(IGuildUser bot, IGuildUser? user, IChannel channel, string what)
    {
        if (channel is not ITextChannel textChannel ||
            bot.GetPermissions(textChannel) is not { SendMessages: true, EmbedLinks: true })
        {
            return $"I need **Send Messages** and **Embed Links** here to post {what}.";
        }

        return user?.GetPermissions(textChannel) is { SendMessages: true, PinMessages: true }
            ? null
            : $"You need **Send Messages** and **Pin Messages** in this channel to post {what} here.";
    }

    private static string MentionOf(IGuildChannel channel)
        => channel is IMentionable mentionable ? mentionable.Mention : $"#{channel.Name}";
}
