namespace ModBot.Configuration;

/// <summary>
/// Web search: <c>/search</c> and "@bot search for …" look things up through Ollama's web search API, and the chatbot
/// writes a short answer from the results. Only the search words are sent to ollama.com; the answer is written by the
/// chatbot's own model (<see cref="ChatOptions"/>). Off until an API key is set.
/// </summary>
public class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>False turns web search off for every server, whatever they chose.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>An Ollama account API key (free; create one at https://ollama.com/settings/keys). Set it with
    /// user-secrets or the <c>OLLAMA_SEARCH_API_KEY</c> environment variable, never in appsettings.json. When empty,
    /// <c>Chat:ApiKey</c> (<c>OLLAMA_API_KEY</c>) is used, since it's the same kind of key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Where Ollama's web search API lives.</summary>
    public string Url { get; set; } = "https://ollama.com/";

    /// <summary>How many results to read for each search (Ollama allows 1-10).</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Minimum time between searches from the same person, so one user can't use up the key's allowance.</summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>How long to wait for the search itself.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Whether searching can work at all: turned on, with a key.</summary>
    public bool IsEnabled => Enabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Url);
}
