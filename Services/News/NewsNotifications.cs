using System.Text.RegularExpressions;
using ModBot.Services.Storage;

namespace ModBot.Services.News;

/// <summary>A chat request to make news silent or notify again: one feed, or (null) every feed in the server.</summary>
public record NewsNotifyRequest(string? Feed, bool Silent);

/// <summary>
/// Turns notifications for a server's news feeds on or off, shared by /news notify and the chatbot ("@bot mute news
/// notifications") so both do the same thing. Per server: feeds belong to one server, so this only ever changes the
/// asking server's feeds.
/// </summary>
public partial class NewsNotifications
{
    private readonly INewsSubscriptionStore _store;

    public NewsNotifications(INewsSubscriptionStore store)
    {
        _store = store;
    }

    /// <summary>Sets <paramref name="feed"/> (or, when null, every feed this server follows) to post silently or
    /// not. Returns what to tell the admin.</summary>
    public async Task<string> SetAsync(ulong guildId, string? feed, bool silent)
    {
        var how = silent ? "🔕 posts silently (no notifications)" : "🔔 notifies people again";
        if (feed is not null)
        {
            var name = feed.Trim().ToLowerInvariant();
            return await _store.UpdateAsync(guildId, name, s => s.Silent = silent)
                ? $"**{name}** {how}."
                : "No followed feed has that name - check `/news list`.";
        }

        var feeds = await _store.GetForGuildAsync(guildId);
        if (feeds.Count == 0)
        {
            return "This server isn't following any news yet. Try `/news follow-game`.";
        }

        foreach (var subscription in feeds)
        {
            await _store.UpdateAsync(guildId, subscription.Name, s => s.Silent = silent);
        }

        return $"All {feeds.Count} news feed{(feeds.Count == 1 ? "" : "s")} here {(silent ? "post silently now" : "notify people again")}.";
    }

    /// <summary>
    /// Spots "mute news notifications", "unmute the news", "turn news notifications on", "make the mw4 feed silent" in
    /// chat, with plain rules like the other admin switches. The whole message has to be the request.
    /// </summary>
    public static NewsNotifyRequest? Read(string message)
    {
        var match = RequestRegex().Match(message.Replace('’', '\''));
        if (!match.Success)
        {
            return null;
        }

        var verb = match.Groups["verb"].Value.ToLowerInvariant();
        var state = match.Groups["state"].Value.ToLowerInvariant();
        var silent = verb is "mute" or "silence" || state is "off" or "silent" or "quiet";
        var feed = match.Groups["feed"].Success ? match.Groups["feed"].Value.ToLowerInvariant() : null;
        return new NewsNotifyRequest(feed, silent);
    }

    /// <summary>The news, or one feed ("the mw4 feed", "the mw4 news").</summary>
    private const string Target =
        @"(?:the\s+)?(?:(?<feed>(?!news\b)[a-z0-9][a-z0-9-]{0,31})\s+(?:feed|news)|news(?:\s+feeds?)?)(?:\s+(?:notifications|pings|posts))?";

    [GeneratedRegex(
        @"^\W*(?:(?:hey|ok|okay|please|pls|can\s+you|could\s+you|would\s+you|go\s+ahead\s+and)[\s,]+)*" +
        @"(?:(?<verb>mute|unmute|silence)\s+" + Target +
        @"|(?<verb>turn|switch)\s+(?<state>on|off)\s+" + Target +
        @"|(?<verb>turn|switch)\s+" + Target + @"\s+(?<state>on|off)" +
        @"|(?<verb>make)\s+" + Target + @"\s+(?<state>silent|quiet|notify|loud))" +
        @"(?:\s+(?:for\s+this\s+server|on\s+this\s+server|here))?(?:[\s,]+(?:now|please|pls))*[\s.!]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RequestRegex();
}
