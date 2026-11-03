using System.Text.RegularExpressions;
using Discord;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<NewsNotifications> _logger;

    public NewsNotifications(INewsSubscriptionStore store, ILogger<NewsNotifications> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Sets <paramref name="feed"/> (or, when null, every feed this server follows) to post silently or
    /// not, logs who changed it, and returns what to tell the admin.</summary>
    /// <param name="by">Who changed it, for the log.</param>
    public async Task<string> SetAsync(ulong guildId, string? feed, bool silent, IUser by)
    {
        var state = silent ? "silent" : "notify";
        if (feed is not null)
        {
            var name = feed.Trim().ToLowerInvariant();
            if (!await _store.UpdateAsync(guildId, name, s => s.Silent = silent))
            {
                return "No followed feed has that name - check `/news list`.";
            }

            _logger.LogInformation("News feed {Feed} set to {State} by {User} ({UserId}).", name, state, by.Username,
                by.Id);
            return $"**{name}** {(silent ? "🔕 posts silently (no notifications)" : "🔔 notifies people again")}.";
        }

        // One locked step for every feed, counting what actually changed.
        var (feeds, changed) = await _store.SetSilentForGuildAsync(guildId, silent);
        if (feeds == 0)
        {
            return "This server isn't following any news yet. Try `/news follow-game`.";
        }

        _logger.LogInformation("All news feeds ({Changed} of {Feeds} changed) set to {State} by {User} ({UserId}).",
            changed, feeds, state, by.Username, by.Id);
        var all = $"{(feeds == 1 ? "The" : $"All {feeds}")} news feed{(feeds == 1 ? "" : "s")} here";
        return changed == 0
            ? $"{all} already {(silent ? "post silently" : "notify people")}."
            : $"{all} {(silent ? "post silently now" : "notify people again")} ({changed} changed).";
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
