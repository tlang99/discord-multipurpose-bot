namespace ModBot.Services.Search;

/// <summary>One web search result: the page's title and address, and the part of it that matched.</summary>
public record WebResult(string Title, string Url, string Content);

/// <summary>Thrown when a search can't be done. <see cref="StatusCode"/> is set for HTTP errors.</summary>
public class WebSearchException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Searches the web. A thin seam over the provider (currently Ollama's web search API), so nothing here knows
/// about Discord and tests can fake it.</summary>
public interface IWebSearch
{
    /// <summary>Up to <paramref name="maxResults"/> results for <paramref name="query"/>, best first.</summary>
    /// <exception cref="WebSearchException">The search failed.</exception>
    Task<IReadOnlyList<WebResult>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default);
}
