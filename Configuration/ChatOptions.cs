namespace ModBot.Configuration;

public class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Turns the chatbot off entirely when false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base address of the Ollama API: https://ollama.com for Ollama's cloud models, or e.g.
    /// http://127.0.0.1:11434 for Ollama running on a machine you control.</summary>
    public string OllamaUrl { get; set; } = "https://ollama.com";

    /// <summary>Needed for ollama.com (create one at https://ollama.com/settings/keys); usually empty for your own
    /// Ollama. Set it with the <c>OLLAMA_API_KEY</c> environment variable or user-secrets, never in appsettings.json.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model name as Ollama knows it. For ollama.com, use a name from https://ollama.com/api/tags.</summary>
    public string Model { get; set; } = "gemma4:31b";

    /// <summary>How many earlier messages in the channel are sent along for context. 0 sends only the question.</summary>
    public int HistoryMessages { get; set; } = 10;

    /// <summary>Minimum time between questions from the same person, so one user can't run up usage.</summary>
    public int CooldownSeconds { get; set; } = 10;

    /// <summary>How many questions are sent to Ollama at once; the rest wait their turn.</summary>
    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>How long to wait for a reply before giving up.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Instructions for the model. <c>{botName}</c> and <c>{serverName}</c> are filled in. Empty uses the
    /// built-in default.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Whether the chatbot should answer at all: turned on, with a model and an address set.</summary>
    public bool IsEnabled => Enabled && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(OllamaUrl);
}
