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
    Task<string> ReplyAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default);
}
