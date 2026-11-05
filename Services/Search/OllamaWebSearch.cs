using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModBot.Common;

namespace ModBot.Services.Search;

/// <summary>
/// Ollama's web search API (<c>POST https://ollama.com/api/web_search</c>, documented at
/// https://docs.ollama.com/capabilities/web-search). The base address, API key and timeout are set on the HttpClient
/// in Program.cs. Only the query is sent.
/// </summary>
public class OllamaWebSearch : IWebSearch
{
    /// <summary>Ollama's own limit on results per search.</summary>
    public const int MaxResultsAllowed = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;

    public OllamaWebSearch(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<WebResult>> SearchAsync(string query, int maxResults,
        CancellationToken cancellationToken = default)
    {
        var request = new SearchRequest(query, Math.Clamp(maxResults, 1, MaxResultsAllowed));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("api/web_search", request, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new WebSearchException($"Couldn't reach the web search at {_httpClient.BaseAddress}: {ex.Message}",
                inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new WebSearchException($"Web search returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            SearchResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException ||
                                       (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new WebSearchException($"Couldn't read the web search's response: {ex.Message}", inner: ex);
            }

            // Only results with a real web address: they're shown as links, so nothing else (javascript:, file:)
            // may get through.
            return (payload?.Results ?? [])
                .Where(r => r is not null && WebLinks.TryParse(r.Url, out _))
                .Select(r => new WebResult((r!.Title ?? "").Trim(), r.Url!.Trim(), (r.Content ?? "").Trim()))
                .ToList();
        }
    }

    private record SearchRequest(string Query, int MaxResults);

    private record SearchResponse(IReadOnlyList<SearchResult?>? Results);

    private record SearchResult(string? Title, string? Url, string? Content);
}
