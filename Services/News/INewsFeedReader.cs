namespace ModBot.Services.News;

/// <summary>
/// Fetches articles from a news feed. Kept independent of Discord.Net so the source format (RSS/Atom today,
/// maybe a JSON API later) can change without touching the poller or slash commands.
/// </summary>
public interface INewsFeedReader
{
    /// <summary>Returns the feed's articles, newest first. Throws if the feed can't be fetched or parsed.</summary>
    Task<IReadOnlyList<NewsArticle>> ReadAsync(string feedUrl, CancellationToken cancellationToken = default);
}
