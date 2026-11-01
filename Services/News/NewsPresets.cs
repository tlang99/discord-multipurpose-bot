using Discord.Interactions;

namespace ModBot.Services.News;

/// <summary>Built-in feeds offered as choices in <c>/news follow-game</c>. Add a game by adding an enum value
/// and a matching entry in <see cref="NewsPresets.All"/>.</summary>
public enum NewsPreset
{
    [ChoiceDisplay("Call of Duty: Modern Warfare 4")]
    ModernWarfare4,
}

/// <param name="IncludeKeywords">Default filter copied onto the subscription when it's created; servers can
/// tweak it afterwards with <c>/news filter-*</c>. Empty means post everything.</param>
public record NewsPresetInfo(
    string Name,
    string DisplayName,
    string FeedUrl,
    IReadOnlyList<string> IncludeKeywords,
    IReadOnlyList<string> ExcludeKeywords);

public static class NewsPresets
{
    /// <summary>Content-update vocabulary shared by live-service shooters.</summary>
    private static readonly string[] UpdateKeywords =
    [
        "update", "patch", "patch notes", "hotfix", "season", "battle pass", "roadmap", "reloaded", "midseason",
        "map", "weapon", "gun", "new mode", "game mode", "playlist", "limited-time", "event", "operator",
        "bundle", "camo", "perk", "killstreak", "balance", "nerf", "buff", "release date", "drop", "dlc",
        "new content", "ranked play",
    ];

    /// <summary>Headlines that tend to be opinion/guides/business/politics rather than "what's new in the game".
    /// (Mode names like DMZ/Zombies aren't include keywords for the same reason - most of those articles are
    /// walkthroughs.)</summary>
    private static readonly string[] NonUpdateKeywords =
    [
        "review", "best", "build", "loadout", "tier list", "how to", "where to", "guide", "explained",
        "player count", "sales", "stock", "politician", "president", "lawsuit", "deal", "discount", "price cut",
        "vs", "should you", "beta",
    ];

    public static readonly IReadOnlyDictionary<NewsPreset, NewsPresetInfo> All = new Dictionary<NewsPreset, NewsPresetInfo>
    {
        [NewsPreset.ModernWarfare4] = new(
            "mw4",
            "Call of Duty: Modern Warfare 4",
            BingNewsSearch("\"Modern Warfare 4\" (update OR season OR \"battle pass\" OR map OR maps OR weapon OR " +
                           "weapons OR patch OR roadmap OR event OR mode)"),
            UpdateKeywords,
            NonUpdateKeywords),
    };

    /// <summary>Bing News is used over Google News because its feed includes each article's summary snippet and a
    /// thumbnail, while Google's only has the headline.</summary>
    /// <remarks>Sorted by date: Bing's default is relevance, which kept returning the same ~10 mostly old articles
    /// for days, so new ones (e.g. a Season 1 announcement) never made it into the feed.</remarks>
    private static string BingNewsSearch(string query) => LegacyBingNewsSearch(query) + SortByDate;

    private const string SortByDate = "&qft=sortbydate%3d%221%22";

    private static string LegacyBingNewsSearch(string query)
        => $"https://www.bing.com/news/search?q={Uri.EscapeDataString(query)}&format=rss&mkt=en-US";

    /// <summary>If <paramref name="feedUrl"/> is a built-in preset's feed URL from an older version of the bot (before
    /// the date sort was added), returns the preset's current URL; otherwise null. Subscriptions store their URL, so
    /// without this, existing follows would keep using the old relevance-sorted feed.</summary>
    public static string? UpgradeLegacyFeedUrl(string feedUrl)
    {
        foreach (var preset in All.Values)
        {
            if (preset.FeedUrl.EndsWith(SortByDate, StringComparison.Ordinal) &&
                string.Equals(feedUrl, preset.FeedUrl[..^SortByDate.Length], StringComparison.Ordinal))
            {
                return preset.FeedUrl;
            }
        }

        return null;
    }
}
