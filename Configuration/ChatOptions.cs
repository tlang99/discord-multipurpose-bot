namespace ModBot.Configuration;

public class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Turns the chatbot off entirely when false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base address of the Ollama API. Defaults to Ollama on this machine; 127.0.0.1 rather than "localhost",
    /// since Windows tries localhost over IPv6 first and Ollama only listens on IPv4 (the same delay Lavalink had).
    /// Use https://ollama.com for Ollama's cloud models instead.</summary>
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Only needed for ollama.com (create one at https://ollama.com/settings/keys); a local Ollama doesn't use
    /// one. Set it with the <c>OLLAMA_API_KEY</c> environment variable or user-secrets, never in appsettings.json.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model name as Ollama knows it: one you've pulled (<c>ollama list</c>), or for ollama.com a name from
    /// https://ollama.com/api/tags.</summary>
    public string Model { get; set; } = "llama3.2";

    /// <summary>How many earlier messages in the channel are sent along for context. 0 sends only the question.</summary>
    public int HistoryMessages { get; set; } = 10;

    /// <summary>Minimum time between questions from the same person, so one user can't run up usage.</summary>
    public int CooldownSeconds { get; set; } = 10;

    /// <summary>How many questions are sent to Ollama at once; the rest wait their turn. One keeps a local Ollama from
    /// competing with itself for the same CPU/GPU.</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    /// <summary>How long to wait for a reply before giving up. Generous because a local Ollama loads the model into
    /// memory on the first question after it's been idle.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Instructions for the model. <c>{botName}</c> and <c>{serverName}</c> are filled in. Empty uses the
    /// built-in default.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Whether the chatbot should answer at all: turned on, with a model and an address set.</summary>
    public bool IsEnabled => Enabled && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(OllamaUrl);
}
