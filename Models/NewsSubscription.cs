namespace ModBot.Models;

/// <summary>
/// A feed a guild follows, and the channel new articles from it get posted to.
/// </summary>
public class NewsSubscription
{
    public ulong GuildId { get; set; }

    /// <summary>Short name, unique per guild (e.g. "mw4"). Used to refer to the subscription in commands.</summary>
    public string Name { get; set; } = string.Empty;

    public string FeedUrl { get; set; } = string.Empty;

    public ulong ChannelId { get; set; }

    /// <summary>If non-empty, only articles whose headline or summary contains one of these get posted.</summary>
    public List<string> IncludeKeywords { get; set; } = new();

    /// <summary>Articles whose headline contains any of these are skipped.</summary>
    public List<string> ExcludeKeywords { get; set; } = new();

    public bool Matches(Services.News.NewsArticle article)
        => Services.News.NewsKeywordFilter.Matches(article, IncludeKeywords, ExcludeKeywords);

    /// <summary>IDs of articles already posted (or already present when the subscription was created), newest
    /// last. Trimmed to a bounded size so the store doesn't grow forever.</summary>
    public List<string> SeenItemIds { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
