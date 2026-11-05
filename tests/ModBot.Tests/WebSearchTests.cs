using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Chat;
using ModBot.Services.Search;
using ModBot.Services.Settings;

namespace ModBot.Tests;

/// <summary>Spotting "search for …" in chat.</summary>
public class SearchRequestReaderTests
{
    [Theory]
    [InlineData("search for the new patch notes", "the new patch notes")]
    [InlineData("Search the web for warzone map changes", "warzone map changes")]
    [InlineData("can you look up the hardpoint map pool please", "the hardpoint map pool")]
    [InlineData("google best smg loadout", "best smg loadout")]
    [InlineData("hey, search: when does season 2 start?", "when does season 2 start?")]
    [InlineData("web search ollama pricing", "ollama pricing")]
    [InlineData("look up who won the cdl champs!", "who won the cdl champs")]
    public void Reads_the_search(string message, string query) => Assert.Equal(query, SearchRequestReader.Read(message));

    [Theory]
    [InlineData("how do I search for a song?")] // a question about searching
    [InlineData("what's the best smg right now")] // sounds like it needs the web, but nobody asked for a search
    [InlineData("I looked up the patch notes yesterday")]
    [InlineData("search for a squad")] // looking for a group: LFG's job
    [InlineData("search for 2 players for ranked")]
    [InlineData("search")]
    [InlineData("look up")]
    public void Ignores_everything_else(string message) => Assert.Null(SearchRequestReader.Read(message));

    [Fact]
    public void Keeps_searches_about_players_that_arent_lfg()
        => Assert.Equal("players of the year 2026", SearchRequestReader.Read("search for players of the year 2026"));

    [Fact]
    public void Cuts_very_long_searches()
        => Assert.Equal(SearchRequestReader.MaxQueryLength,
            SearchRequestReader.Read("search for " + new string('a', 500))!.Length);

    [Fact]
    public async Task Is_a_chat_action_checked_before_the_others()
    {
        // "Remind me in 5 minutes" inside a search is still a search, and the model is never asked.
        using var store = new TempReminderStore();
        var parser = new ChatActionParser(new FakeChat("""{"action":"play_music"}""", failJson: true),
            new Services.Reminders.ReminderService(store.Store, new FakeGuildSettings(),
                Options.Create(new ReminderOptions()), NullLogger<Services.Reminders.ReminderService>.Instance),
            NullLogger<ChatActionParser>.Instance);

        var action = await parser.ParseAsync("search for how to remind me in 5 minutes to drink water");

        Assert.Equal(new WebSearchAction("how to remind me in 5 minutes to drink water"), action);
    }
}

/// <summary>Reading Ollama's web search response.</summary>
public class OllamaWebSearchTests
{
    private sealed class FixedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? SentBody { get; private set; }
        public Uri? SentTo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SentTo = request.RequestUri;
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (OllamaWebSearch Search, FixedResponse Handler) Make(HttpStatusCode status, string body)
    {
        var handler = new FixedResponse(status, body);
        return (new OllamaWebSearch(new HttpClient(handler) { BaseAddress = new Uri("https://ollama.com/") }), handler);
    }

    [Fact]
    public async Task Sends_only_the_query_and_reads_the_results()
    {
        var (search, handler) = Make(HttpStatusCode.OK, """
            {"results":[
              {"title":"Patch notes","url":"https://example.com/patch","content":"New maps."},
              {"title":"Sneaky","url":"javascript:alert(1)","content":"x"},
              {"title":null,"url":"http://example.org/b","content":null}
            ]}
            """);

        var results = await search.SearchAsync("patch notes", 50);

        Assert.Equal(new Uri("https://ollama.com/api/web_search"), handler.SentTo);
        Assert.Equal("""{"query":"patch notes","max_results":10}""", handler.SentBody); // capped at Ollama's limit
        Assert.Equal([new WebResult("Patch notes", "https://example.com/patch", "New maps."),
            new WebResult("", "http://example.org/b", "")], results); // only real web addresses
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    public async Task Reports_http_errors_with_their_status(HttpStatusCode status, int code)
    {
        var (search, _) = Make(status, """{"error":"nope"}""");

        var error = await Assert.ThrowsAsync<WebSearchException>(() => search.SearchAsync("x", 5));

        Assert.Equal(code, error.StatusCode);
    }

    [Fact]
    public async Task A_garbled_response_is_a_search_failure()
    {
        var (search, _) = Make(HttpStatusCode.OK, "<html>not json</html>");

        await Assert.ThrowsAsync<WebSearchException>(() => search.SearchAsync("x", 5));
    }
}

/// <summary>Turning search results into the posted answer and its sources embed.</summary>
public class WebSearchAnswererTests
{
    private sealed class FakeSearch(IReadOnlyList<WebResult>? results = null, int? failWith = null) : IWebSearch
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<WebResult>> SearchAsync(string query, int maxResults,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return failWith is { } status
                ? throw new WebSearchException("boom", status)
                : Task.FromResult(results ?? []);
        }
    }

    private static readonly IReadOnlyList<WebResult> TwoResults =
    [
        new("Season 2 patch notes", "https://www.example.com/patch(2)", "Season 2 adds three maps."),
        new("Map list", "https://maps.example.org/list", "The maps are A, B and C."),
    ];

    private static WebSearchAnswerer Answerer(IWebSearch search, IChatService chat, string apiKey = "key",
        int cooldown = 30)
    {
        var chatOptions = Options.Create(new ChatOptions());
        return new WebSearchAnswerer(search, chat, new ChatPersonality(new NoServerPersonality(), chatOptions),
            Options.Create(new SearchOptions { ApiKey = apiKey, CooldownSeconds = cooldown }), chatOptions,
            NullLogger<WebSearchAnswerer>.Instance);
    }

    [Fact]
    public async Task Answers_in_its_own_words_with_numbered_sources_in_an_embed()
    {
        var answerer = Answerer(new FakeSearch(TwoResults),
            new FakeChat("Season 2 brings three new maps [1]. See https://example.com/patch or [this](https://x.y)."));

        var found = await answerer.AnswerAsync(1, "Server", "ModBot", 2, "season 2 maps");

        // Links come out of the answer: the sources are in the embed.
        Assert.Equal("Season 2 brings three new maps [1]. See  or this.", found.Text);
        Assert.NotNull(found.Embed);
        Assert.Equal("🔎 season 2 maps", found.Embed!.Title);
        Assert.Equal(
            "**1.** [Season 2 patch notes](https://www.example.com/patch%282%29) · example.com\n" +
            "**2.** [Map list](https://maps.example.org/list) · maps.example.org", found.Embed.Description);
        Assert.Contains("answer written by ModBot", found.Embed.Footer?.Text);
    }

    [Fact]
    public async Task Shows_the_results_themselves_when_the_chatbot_is_down()
    {
        var answerer = Answerer(new FakeSearch(TwoResults), new FakeChat("", fail: true));

        var found = await answerer.AnswerAsync(1, "Server", "ModBot", 2, "season 2 maps");

        Assert.StartsWith("I found these", found.Text);
        Assert.Contains("Season 2 adds three maps", found.Embed!.Description); // what each result said
    }

    [Fact]
    public async Task One_search_per_person_per_cooldown()
    {
        var search = new FakeSearch(TwoResults);
        var answerer = Answerer(search, new FakeChat("An answer [1]."));

        await answerer.AnswerAsync(1, "Server", "ModBot", 2, "first");
        var second = await answerer.AnswerAsync(1, "Server", "ModBot", 2, "second");
        var someoneElse = await answerer.AnswerAsync(1, "Server", "ModBot", 3, "theirs");

        Assert.StartsWith("⏳", second.Text);
        Assert.Null(second.Embed);
        Assert.NotNull(someoneElse.Embed);
        Assert.Equal(2, search.Calls);
    }

    [Theory]
    [InlineData(401, "key isn't working")]
    [InlineData(429, "too many searches")]
    [InlineData(500, "couldn't search")]
    public async Task Says_what_went_wrong_without_details(int status, string expected)
    {
        var found = await Answerer(new FakeSearch(failWith: status), new FakeChat("x"))
            .AnswerAsync(1, "Server", "ModBot", 2, "q");

        Assert.Contains(expected, found.Text);
        Assert.Null(found.Embed);
    }

    [Fact]
    public async Task Says_so_when_nothing_was_found()
    {
        var found = await Answerer(new FakeSearch([]), new FakeChat("x")).AnswerAsync(1, "Server", "ModBot", 2, "zzz");

        Assert.Contains("found nothing", found.Text);
    }

    [Fact]
    public async Task Without_a_key_it_says_it_isnt_set_up_and_never_searches()
    {
        var search = new FakeSearch(TwoResults);
        var answerer = Answerer(search, new FakeChat("x"), apiKey: "");

        var found = await answerer.AnswerAsync(1, "Server", "ModBot", 2, "q");

        Assert.Contains("isn't set up", found.Text);
        Assert.NotNull(answerer.SetupProblem);
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public void Source_titles_cant_become_markdown()
    {
        var embed = WebSearchAnswerer.BuildEmbed("q",
            [new WebResult("Click [here](https://evil.example) *now*", "https://ok.example/", "")], "ModBot",
            withSnippets: false);

        // Escaped, so it's one link to the real result, not a link to evil.example inside it.
        Assert.StartsWith(@"**1.** [Click \[here\]", embed.Description);
        Assert.DoesNotContain("](https://evil.example)", embed.Description);
        Assert.EndsWith("](https://ok.example/) · ok.example", embed.Description);
    }
}

/// <summary>Web search as a server feature.</summary>
public class WebSearchFeatureTests
{
    [Fact]
    public void Is_on_by_default()
    {
        Assert.False(ServerSettings.IsOptIn(ServerFeature.WebSearch));
        Assert.True(new ServerSettings().IsEnabled(ServerFeature.WebSearch));
    }

    [Theory]
    [InlineData("disable web search", false)]
    [InlineData("enable searching", true)]
    [InlineData("turn off search for this server", false)]
    public void Admins_can_switch_it_from_chat(string message, bool on)
        => Assert.Equal(new FeatureSwitchRequest(ServerFeature.WebSearch, on), SettingsRequestReader.Read(message));

    [Fact]
    public void Turning_it_off_isnt_mistaken_for_a_search()
        => Assert.Null(SearchRequestReader.Read("disable web search"));
}
