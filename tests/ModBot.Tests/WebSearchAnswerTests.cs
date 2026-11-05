using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.Search;

namespace ModBot.Tests;

/// <summary>Turning search results into the posted answer: a news-style embed.</summary>
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

    /// <summary>Every page answers with this HTML.</summary>
    private sealed class FixedPage(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
                RequestMessage = request,
            });
    }

    private const string PageWithPicture = """<meta property="og:image" content="/img/cover.jpg">""";

    private static readonly IReadOnlyList<WebResult> TwoResults =
    [
        new("Season 2 patch notes", "https://www.example.com/patch", "Season 2 adds three maps."),
        new("Map list", "https://maps.example.org/list", "The maps are A, B and C."),
    ];

    private static WebSearchAnswerer Answerer(IWebSearch search, IChatService chat, string apiKey = "key",
        string page = PageWithPicture)
    {
        var chatOptions = Options.Create(new ChatOptions());
        return new WebSearchAnswerer(search, chat, new ChatPersonality(new NoServerPersonality(), chatOptions),
            new PageImageFinder(new HttpClient(new FixedPage(page)), NullLogger<PageImageFinder>.Instance),
            Options.Create(new SearchOptions { ApiKey = apiKey }), chatOptions, NullLogger<WebSearchAnswerer>.Instance);
    }

    [Fact]
    public async Task Is_posted_like_a_news_post()
    {
        var answerer = Answerer(new FakeSearch(TwoResults),
            new FakeChat("Season 2 brings three new maps [1]. See https://example.com/patch or [this](https://x.y)."));

        var found = await answerer.AnswerAsync(1, "Server", "ModBot", "season 2 maps");

        // All in the embed: titled with the top result and linking to it, the answer as its text (links and result
        // numbers taken out), the page's own picture, and the news footer.
        Assert.Equal("", found.Text);
        var embed = found.Embed!;
        Assert.Equal("Season 2 patch notes", embed.Title);
        Assert.Equal("https://www.example.com/patch", embed.Url);
        Assert.Equal("Season 2 brings three new maps. See or this.", embed.Description);
        Assert.Equal("https://www.example.com/img/cover.jpg", embed.Image?.Url);
        Assert.Equal("example.com • Web search • AI write-up by ModBot", embed.Footer?.Text);
    }

    [Fact]
    public Task Shows_the_top_results_own_summary_when_the_chatbot_is_down()
        => ShowsTheResultsOwnSummary(new FakeChat("", fail: true));

    [Fact]
    public Task Shows_the_top_results_own_summary_when_the_answer_was_only_links()
        => ShowsTheResultsOwnSummary(new FakeChat("https://example.com/patch [1]"));

    private static async Task ShowsTheResultsOwnSummary(IChatService chat)
    {
        var found = await Answerer(new FakeSearch(TwoResults), chat).AnswerAsync(1, "Server", "ModBot", "season 2 maps");

        Assert.Equal("Season 2 patch notes", found.Embed!.Title);
        Assert.Contains("Season 2 adds three maps", found.Embed.Description);
        Assert.Equal("example.com • Web search", found.Embed.Footer?.Text); // no "AI write-up": there wasn't one
    }

    [Fact]
    public async Task A_page_without_a_picture_just_has_none()
    {
        var found = await Answerer(new FakeSearch(TwoResults), new FakeChat("An answer."), page: "<html></html>")
            .AnswerAsync(1, "Server", "ModBot", "q");

        Assert.Null(found.Embed!.Image);
    }

    [Fact]
    public void One_search_per_person_per_cooldown()
    {
        var answerer = Answerer(new FakeSearch(TwoResults), new FakeChat("An answer."));

        Assert.Null(answerer.TryStart(1, 2));
        Assert.StartsWith("⏳", answerer.TryStart(1, 2));
        Assert.Null(answerer.TryStart(1, 3)); // someone else
        Assert.Null(answerer.TryStart(4, 2)); // the same person in another server
    }

    [Fact]
    public async Task Only_one_of_two_searches_at_the_same_moment_gets_the_turn()
    {
        var answerer = Answerer(new FakeSearch(TwoResults), new FakeChat("x"));

        var starts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => answerer.TryStart(1, 2))));

        Assert.Single(starts, s => s is null);
    }

    [Theory]
    [InlineData(401, "key isn't working")]
    [InlineData(429, "too many searches")]
    [InlineData(500, "couldn't search")]
    public async Task Says_what_went_wrong_without_details(int status, string expected)
    {
        var found = await Answerer(new FakeSearch(failWith: status), new FakeChat("x"))
            .AnswerAsync(1, "Server", "ModBot", "q");

        Assert.Contains(expected, found.Text);
        Assert.Null(found.Embed);
    }

    [Fact]
    public async Task Says_so_when_nothing_was_found()
    {
        var found = await Answerer(new FakeSearch([]), new FakeChat("x")).AnswerAsync(1, "Server", "ModBot", "zzz");

        Assert.Contains("found nothing", found.Text);
    }

    [Fact]
    public async Task Without_a_key_it_says_it_isnt_set_up_and_never_searches()
    {
        var search = new FakeSearch(TwoResults);
        var answerer = Answerer(search, new FakeChat("x"), apiKey: "");

        var found = await answerer.AnswerAsync(1, "Server", "ModBot", "q");

        Assert.Contains("isn't set up", found.Text);
        Assert.Contains("isn't set up", answerer.TryStart(1, 2));
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public void Needs_an_https_address() // or the key would go over plain http
        => Assert.Contains("https", new SearchOptions { ApiKey = "key", Url = "http://ollama.com/" }.SetupProblem);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_blank_address_means_ollamas_own(string? url)
    {
        var options = new SearchOptions { ApiKey = "key", Url = url! };

        Assert.True(options.UsesDefaultUrl);
        Assert.Null(options.SetupProblem);
    }

    [Fact]
    public void Is_ready_with_a_key_and_the_default_address()
        => Assert.Null(new SearchOptions { ApiKey = "key" }.SetupProblem);

    [Fact]
    public void A_summary_from_a_page_cant_become_markdown()
    {
        var embed = WebSearchAnswerer.BuildEmbed(
            new WebResult("Title", "https://ok.example/", "Click [here](https://evil.example) *now*"), answer: null,
            "ModBot", imageUrl: null);

        Assert.DoesNotContain("](https://evil.example)", embed.Description);
    }
}

/// <summary>Finding a page's own preview picture.</summary>
public class PageImageFinderTests
{
    private static readonly Uri Page = new("https://news.example.com/articles/1");

    [Theory]
    [InlineData("""<meta property="og:image" content="https://cdn.example.com/a.jpg">""",
        "https://cdn.example.com/a.jpg")]
    [InlineData("<META content='/b.png' property='og:image'>", "https://news.example.com/b.png")] // relative
    [InlineData("""<meta name="twitter:image" content="https://cdn.example.com/c.jpg?w=1&amp;h=2">""",
        "https://cdn.example.com/c.jpg?w=1&h=2")]
    public void Reads_the_preview_image(string html, string expected)
        => Assert.Equal(expected, PageImageFinder.FromHtml($"<head>{html}</head>", Page));

    [Theory]
    [InlineData("""<meta property="og:image" content="http://cdn.example.com/a.jpg">""")] // Discord loads it: https only
    [InlineData("""<meta property="og:image" content="javascript:alert(1)">""")]
    [InlineData("""<meta property="og:title" content="https://cdn.example.com/a.jpg">""")]
    [InlineData("<p>no tags</p>")]
    public void Ignores_anything_else(string html) => Assert.Null(PageImageFinder.FromHtml(html, Page));
}

/// <summary>The shared check for links from outside.</summary>
public class WebLinksTests
{
    [Theory]
    [InlineData("https://example.com/a", true, true)]
    [InlineData("http://example.com/a", true, false)]
    [InlineData("javascript:alert(1)", false, false)]
    [InlineData("/relative", false, false)]
    [InlineData(null, false, false)]
    public void Accepts_only_web_links(string? value, bool anyWeb, bool https)
    {
        Assert.Equal(anyWeb, WebLinks.TryParse(value, out _));
        Assert.Equal(https, WebLinks.TryParse(value, out _, httpsOnly: true));
    }

    [Fact]
    public void Names_the_site_without_www()
        => Assert.Equal("example.com", WebLinks.SiteOf(new Uri("https://www.example.com/a")));
}
