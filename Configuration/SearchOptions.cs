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

    /// <summary>Ollama's own web search API.</summary>
    public const string DefaultUrl = "https://ollama.com/";

    /// <summary>An Ollama account API key (free; create one at https://ollama.com/settings/keys). Set it with
    /// user-secrets or the <c>OLLAMA_SEARCH_API_KEY</c> environment variable, never in appsettings.json. When empty
    /// and <see cref="Url"/> is ollama.com's, <c>Chat:ApiKey</c> (<c>OLLAMA_API_KEY</c>) is used, since it's the same
    /// kind of key; it's never sent anywhere else.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Where Ollama's web search API lives. Must be https, since the key goes with every search.</summary>
    public string Url
    {
        get => _url;
        set => _url = string.IsNullOrWhiteSpace(value) ? DefaultUrl : value.Trim(); // blank or null in config: the default
    }

    private string _url = DefaultUrl;

    /// <summary>How many results to read for each search (Ollama allows 1-10).</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Minimum time between searches from the same person, so one user can't use up the key's allowance.</summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>How long to wait for the search itself.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Whether <see cref="Url"/> is Ollama's own, the only place the chatbot's key may be sent.</summary>
    public bool UsesDefaultUrl => IsOllamaCom(Url);

    /// <summary>Whether <paramref name="url"/> is Ollama's cloud (https://ollama.com).</summary>
    public static bool IsOllamaCom(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           uri.Host.Equals("ollama.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why searching can't work on this bot at all (turned off, no key, or a bad address), or null if it
    /// can.</summary>
    public string? SetupProblem
        => !Enabled ? "Web search is turned off in the bot's own settings."
            : !Uri.TryCreate(Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
                ? "Web search isn't set up right: Search:Url has to be an https address."
            : string.IsNullOrWhiteSpace(ApiKey)
                ? "Web search isn't set up on this bot yet: whoever runs it needs to add an Ollama API key (see the README)."
            : null;

    /// <summary>Whether searching can work at all.</summary>
    public bool IsEnabled => SetupProblem is null;
}
