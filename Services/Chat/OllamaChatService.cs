using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Services.Chat;

/// <summary>
/// Calls Ollama's <c>/api/chat</c> endpoint. Works the same against ollama.com (with an API key) or a self-hosted
/// Ollama; the base address, key and timeout are set on the HttpClient in Program.cs.
/// </summary>
public class OllamaChatService : IChatService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly ChatRequestLimiter _limiter;
    private readonly ChatOptions _options;

    public OllamaChatService(HttpClient httpClient, ChatRequestLimiter limiter, IOptions<ChatOptions> options)
    {
        _httpClient = httpClient;
        _limiter = limiter;
        _options = options.Value;
    }

    public async Task<string> ReplyAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<OllamaMessage> { new("system", systemPrompt) };
        messages.AddRange(conversation.Select(t => new OllamaMessage(t.Role == ChatRole.Assistant ? "assistant" : "user",
            t.Content)));

        return await SendAsync(new OllamaChatRequest(_options.Model, messages, Stream: false), cancellationToken);
    }

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        // Ollama's structured outputs: "format" takes a JSON Schema and the model's answer is constrained to it.
        using var schema = JsonDocument.Parse(jsonSchema);
        var request = new OllamaChatRequest(_options.Model,
            [new("system", systemPrompt), new("user", userMessage)],
            Stream: false,
            Format: schema.RootElement.Clone(),
            Options: new OllamaRequestOptions(Temperature: 0));
        return await SendAsync(request, cancellationToken);
    }

    private async Task<string> SendAsync(OllamaChatRequest request, CancellationToken cancellationToken)
    {
        // One slot per request, shared by chat and news, so a local Ollama isn't asked to do too much at once.
        using var slot = await _limiter.AcquireAsync(cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("api/chat", request, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ChatServiceException($"Couldn't reach Ollama at {_httpClient.BaseAddress}: {ex.Message}", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                throw new ChatServiceException($"Ollama returned HTTP {(int)response.StatusCode}: {error}",
                    (int)response.StatusCode);
            }

            // A malformed body, or the connection dropping mid-read, becomes the same "the model failed" error as
            // everything else here, so callers only have one exception type to handle.
            OllamaChatResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException
                                           or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new ChatServiceException($"Couldn't read Ollama's response: {ex.Message}", inner: ex);
            }

            // Reasoning models put their thinking in message.thinking, separately, so content is just the answer.
            var reply = payload?.Message?.Content?.Trim();
            if (string.IsNullOrEmpty(reply))
            {
                throw new ChatServiceException("Ollama returned an empty reply.");
            }

            return reply;
        }
    }

    /// <summary>Ollama errors are usually <c>{"error": "..."}</c>; fall back to the raw body, cut short.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            if (JsonSerializer.Deserialize<OllamaError>(body, JsonOptions)?.Error is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. an HTML error page from a proxy).
        }

        return body.Length > 200 ? body[..200] + "..." : body;
    }

    private record OllamaMessage(string Role, string Content);

    private record OllamaChatRequest(string Model, IReadOnlyList<OllamaMessage> Messages, bool Stream,
        JsonElement? Format = null, OllamaRequestOptions? Options = null);

    private record OllamaRequestOptions(double? Temperature = null);

    private record OllamaChatResponse(OllamaMessage? Message);

    private record OllamaError(string? Error);
}
