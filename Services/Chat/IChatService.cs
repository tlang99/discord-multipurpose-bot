namespace ModBot.Services.Chat;

public enum ChatRole
{
    User,
    Assistant,
}

/// <summary>One message of the conversation sent to the model.</summary>
public record ChatTurn(ChatRole Role, string Content);

/// <summary>Thrown when the chat backend can't produce a reply. <see cref="StatusCode"/> is set for HTTP errors.</summary>
public class ChatServiceException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Gets a chatbot reply from a language model. A thin seam over the backend (currently Ollama), so switching
/// providers means a new implementation and one line in Program.cs; nothing here knows about Discord.
/// </summary>
public interface IChatService
{
    /// <summary>Returns the model's reply to <paramref name="conversation"/> (oldest first, ending with the question).</summary>
    /// <exception cref="ChatServiceException">The backend failed or returned nothing usable.</exception>
    /// <param name="timeout">How long the model itself may take, counted from when the request is actually sent (time
    /// spent waiting for a free slot doesn't count). Null uses the client's overall timeout. Running out throws
    /// <see cref="ChatServiceException"/>.</param>
    Task<string> ReplyAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null);

    /// <summary>Asks for an answer that matches <paramref name="jsonSchema"/> (a JSON Schema object) and returns the
    /// raw JSON. Deterministic (temperature 0), for classifying and extracting rather than chatting.</summary>
    /// <exception cref="ChatServiceException">The backend failed or returned nothing usable.</exception>
    Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, string jsonSchema,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the model ready to answer (a local Ollama loads it into memory, which takes seconds after it's been
    /// idle), so the next question isn't kept waiting for that. Never throws: it's only a head start.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
