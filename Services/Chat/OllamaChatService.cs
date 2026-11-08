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

    /// <summary>When Ollama will unload the model (ticks, UTC), going by the keep-alive of the last request it
    /// answered.</summary>
    private long _loadedUntil;

    /// <summary>A warm-up asked for the model to stay loaded until then (ticks, UTC), so every request until then
    /// asks Ollama for at least that long.</summary>
    private long _holdUntil;

    public OllamaChatService(HttpClient httpClient, ChatRequestLimiter limiter, IOptions<ChatOptions> options)
    {
        _httpClient = httpClient;
        _limiter = limiter;
        _options = options.Value;
    }

    public async Task<string> ReplyAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var messages = new List<OllamaMessage> { new("system", systemPrompt) };
        messages.AddRange(conversation.Select(t => new OllamaMessage(t.Role == ChatRole.Assistant ? "assistant" : "user",
            t.Content)));

        return await SendAsync(new OllamaChatRequest(_options.Model, messages, Stream: false), cancellationToken, timeout);
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

    public async Task WarmUpAsync(CancellationToken cancellationToken = default, TimeSpan? keepLoadedFor = null)
    {
        // Only a local Ollama unloads models (on ollama.com it'd be a call for nothing).
        if (SearchOptions.IsOllamaCom(_options.OllamaUrl))
        {
            return;
        }

        if (keepLoadedFor is { } hold)
        {
            // Replaces the last hold, so one that's no longer wanted (the song was skipped) can be cut short. Nothing
            // is sent to shorten it: Ollama goes on by the last keep-alive it was sent until the next request (a
            // question, or another warm-up) sends the shorter one, so the memory is freed at most that much later.
            Interlocked.Exchange(ref _holdUntil, hold > TimeSpan.Zero ? DateTime.UtcNow.Add(hold).Ticks : 0);
            if (hold <= TimeSpan.Zero)
            {
                return; // just ending it: the next request sends the usual keep-alive, and Ollama goes by that
            }
        }

        // Already loaded for long enough: nothing to do.
        if (IsLoadedLongEnough())
        {
            return;
        }

        // Ollama loads the model (or resets how long it stays) for a chat request with no messages, and answers nothing.
        try
        {
            using var slot = await _limiter.AcquireAsync(cancellationToken);
            if (IsLoadedLongEnough())
            {
                return; // a question (or another warm-up) loaded it while this one waited
            }

            var (keepAlive, keptFor) = KeepAlive();
            using var response = await _httpClient.PostAsJsonAsync("api/chat",
                Prepare(new OllamaChatRequest(_options.Model, [], Stream: false), keepAlive), JsonOptions,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                MarkLoaded(keptFor);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Only a head start: the real question reports any problem.
        }
    }

    /// <summary>Ollama's own keep-alive, used when Chat:KeepAliveMinutes leaves it to Ollama.</summary>
    private const int OllamaDefaultKeepAliveMinutes = 5;

    /// <summary>The keep-alive to send now, with how long that keeps the model loaded.</summary>
    private (string? Param, TimeSpan For) KeepAlive()
        => KeepAliveFor(_options.KeepAliveMinutes, new DateTime(Interlocked.Read(ref _holdUntil), DateTimeKind.Utc),
            DateTime.UtcNow);

    /// <summary>
    /// The keep-alive for a request sent at <paramref name="now"/>: Chat:KeepAliveMinutes (<paramref
    /// name="configuredMinutes"/>; 0 or less leaves Ollama's own default), or longer if a warm-up asked for the model
    /// to stay loaded until <paramref name="holdUntil"/>. Each request resets how long Ollama keeps the model, so a
    /// question asked during a hold carries the rest of the hold too. The param is as Ollama wants it ("300s"), or
    /// null for its default.
    /// </summary>
    public static (string? Param, TimeSpan For) KeepAliveFor(int configuredMinutes, DateTime holdUntil, DateTime now)
    {
        var configured = TimeSpan.FromMinutes(configuredMinutes > 0 ? configuredMinutes : OllamaDefaultKeepAliveMinutes);
        var held = holdUntil - now;
        if (held > configured)
        {
            var seconds = (int)Math.Ceiling(held.TotalSeconds);
            return ($"{seconds}s", TimeSpan.FromSeconds(seconds));
        }

        return (configuredMinutes > 0 ? $"{configuredMinutes}m" : null, configured);
    }

    /// <summary><paramref name="request"/> with what every request carries: the keep-alive and, with Chat:UseGpu off,
    /// keeping the model off the graphics card. Every request must say so, warm-ups too: one that didn't would have
    /// Ollama load the model onto the graphics card again.</summary>
    private OllamaChatRequest Prepare(OllamaChatRequest request, string? keepAlive)
        => request with
        {
            KeepAlive = keepAlive,
            Options = _options.UseGpu || SearchOptions.IsOllamaCom(_options.OllamaUrl)
                ? request.Options
                : (request.Options ?? new OllamaRequestOptions()) with { NumGpu = 0 },
        };

    private void MarkLoaded(TimeSpan keptFor)
        => Interlocked.Exchange(ref _loadedUntil, DateTime.UtcNow.Add(keptFor).Ticks);

    /// <summary>The model's loaded (for at least another half a minute), and until the end of any hold.</summary>
    private bool IsLoadedLongEnough()
    {
        var spare = TimeSpan.FromSeconds(30).Ticks;
        var wanted = Math.Max(DateTime.UtcNow.Ticks + spare, Interlocked.Read(ref _holdUntil) - spare);
        return Interlocked.Read(ref _loadedUntil) >= wanted;
    }

    private async Task<string> SendAsync(OllamaChatRequest request, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var (keepAlive, keptFor) = KeepAlive();
        request = Prepare(request, keepAlive);
        // One slot per request, shared by chat and news, so a local Ollama isn't asked to do too much at once.
        using var slot = await _limiter.AcquireAsync(cancellationToken);

        // The timeout starts once the slot is ours, so waiting behind other requests doesn't count against the model.
        // Running out cancels the request; the filters below check the caller's own token, so it surfaces as a
        // ChatServiceException like any other model failure.
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            requestCts.CancelAfter(limit);
        }

        var requestToken = requestCts.Token;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("api/chat", request, JsonOptions, requestToken);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ChatServiceException($"Couldn't reach Ollama at {_httpClient.BaseAddress}: {ex.Message}", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, requestToken);
                throw new ChatServiceException($"Ollama returned HTTP {(int)response.StatusCode}: {error}",
                    (int)response.StatusCode);
            }

            // A malformed body, or the connection dropping mid-read, becomes the same "the model failed" error as
            // everything else here, so callers only have one exception type to handle.
            OllamaChatResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, requestToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException ||
                                       (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new ChatServiceException($"Couldn't read Ollama's response: {ex.Message}", inner: ex);
            }

            // Reasoning models put their thinking in message.thinking, separately, so content is just the answer.
            var reply = payload?.Message?.Content?.Trim();
            MarkLoaded(keptFor); // the model's loaded now
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
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Only used to describe the failure; the status code is enough on its own.
            return "(couldn't read the error details)";
        }

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

    /// <param name="KeepAlive">How long Ollama keeps the model loaded after this request (its own default is 5
    /// minutes).</param>
    private record OllamaChatRequest(string Model, IReadOnlyList<OllamaMessage> Messages, bool Stream,
        JsonElement? Format = null, OllamaRequestOptions? Options = null, string? KeepAlive = null);

    /// <param name="NumGpu">How many of the model's layers go on the graphics card; 0 keeps it all on the processor.
    /// Null leaves it to Ollama.</param>
    private record OllamaRequestOptions(double? Temperature = null, int? NumGpu = null);

    private record OllamaChatResponse(OllamaMessage? Message);

    private record OllamaError(string? Error);
}
