using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Chat;

namespace ModBot.Tests;

/// <summary>What's sent to Ollama: keeping the model off the graphics card (Chat:UseGpu), and how long it's kept
/// loaded (a DJ mode hold).</summary>
public class OllamaRequestTests
{
    /// <summary>Answers every request with a short reply, and keeps each request's body.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonElement> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Sent.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"role":"assistant","content":"hi"}}""", Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static (OllamaChatService Chat, RecordingHandler Handler) Make(bool useGpu,
        string url = "http://127.0.0.1:11434")
    {
        var options = Options.Create(new ChatOptions { UseGpu = useGpu, OllamaUrl = url, KeepAliveMinutes = 5 });
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
        return (new OllamaChatService(http, new ChatRequestLimiter(options), options), handler);
    }

    private static int? NumGpu(JsonElement sent)
        => sent.TryGetProperty("options", out var options) && options.TryGetProperty("num_gpu", out var numGpu)
            ? numGpu.GetInt32()
            : null;

    private static string? KeepAlive(JsonElement sent)
        => sent.TryGetProperty("keep_alive", out var keepAlive) ? keepAlive.GetString() : null;

    [Fact]
    public async Task With_the_gpu_off_every_request_says_so_warm_ups_too()
    {
        var (chat, handler) = Make(useGpu: false);

        await chat.WarmUpAsync();
        await chat.ReplyAsync("system", [new ChatTurn(ChatRole.User, "hello")]);
        await chat.CompleteJsonAsync("system", "hello", """{"type":"object"}""");

        Assert.Equal(3, handler.Sent.Count);
        Assert.All(handler.Sent, sent => Assert.Equal(0, NumGpu(sent)));
    }

    [Fact]
    public async Task With_the_gpu_on_it_is_left_to_Ollama()
    {
        var (chat, handler) = Make(useGpu: true);

        await chat.ReplyAsync("system", [new ChatTurn(ChatRole.User, "hello")]);

        Assert.Null(NumGpu(Assert.Single(handler.Sent)));
    }

    [Fact]
    public async Task Ollama_com_never_gets_it()
    {
        var (chat, handler) = Make(useGpu: false, url: "https://ollama.com");

        await chat.ReplyAsync("system", [new ChatTurn(ChatRole.User, "hello")]);

        Assert.Null(NumGpu(Assert.Single(handler.Sent)));
    }

    [Fact]
    public async Task A_hold_can_be_cut_short()
    {
        var (chat, handler) = Make(useGpu: true);

        await chat.WarmUpAsync(keepLoadedFor: TimeSpan.FromMinutes(15));
        Assert.EndsWith("s", KeepAlive(handler.Sent[^1])); // the hold, in seconds: longer than the usual 5m

        await chat.WarmUpAsync(keepLoadedFor: TimeSpan.Zero);
        Assert.Single(handler.Sent); // ending a hold loads nothing

        await chat.ReplyAsync("system", [new ChatTurn(ChatRole.User, "hello")]);
        Assert.Equal("5m", KeepAlive(handler.Sent[^1])); // back to the usual keep-alive
    }

    [Fact]
    public async Task A_new_hold_replaces_a_longer_one()
    {
        var (chat, handler) = Make(useGpu: true);

        await chat.WarmUpAsync(keepLoadedFor: TimeSpan.FromMinutes(20));
        await chat.WarmUpAsync(keepLoadedFor: TimeSpan.FromMinutes(8)); // a shorter song now: not 20 minutes any more
        await chat.ReplyAsync("system", [new ChatTurn(ChatRole.User, "hello")]);

        var seconds = int.Parse(KeepAlive(handler.Sent[^1])!.TrimEnd('s'));
        Assert.InRange(seconds, 7 * 60, 8 * 60 + 1);
    }
}
