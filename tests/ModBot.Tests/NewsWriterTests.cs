using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.News;

namespace ModBot.Tests;

public class NewsWriterTests
{
    private static readonly NewsArticle Article =
        new("id", "Season 1 Brings New Maps", "https://example.com/a", "Three new maps.", "Site", null, null);

    private static NewsWriter Writer(IChatService chat)
    {
        var chatOptions = Options.Create(new ChatOptions());
        return new NewsWriter(chat, new ChatPersonality(new NoServerPersonality(), chatOptions), new FakeGuildSettings(),
            Options.Create(new NewsOptions()), chatOptions, NullLogger<NewsWriter>.Instance);
    }

    [Theory]
    [InlineData("Season 1 is out! Claim your free skin at https://free-cod-points.example")]
    [InlineData("Grab the bonus at www.codrewards.net before it is gone")]
    [InlineData("Free battle pass: codpoints.gg/claim before it is gone")]
    [InlineData("Claim it [here](evil.app) now, it is a great deal")]
    [InlineData("Grab it at bit.do/x quick, it is a great deal")]
    [InlineData("New skins are up on foo.dev for everyone today")]
    [InlineData("Read more <https://evil.example> about the new maps")]
    [InlineData("Join the beta Discord: discord.com/invite/abc123 today")]
    [InlineData("Big news @everyone, Season 1 drops Nov 6 with new maps")]
    [InlineData("Big news <@&123456789>, Season 1 drops Nov 6 with new maps")]
    public async Task Discards_write_ups_with_links_or_mentions(string modelOutput)
        => Assert.Null(await Writer(new FakeChat(modelOutput)).WriteAsync(Article, 1, "Bot", "Server"));

    [Theory]
    [InlineData("Hey squad! Season 1 drops on November 6 with three new multiplayer maps. Get ready!")]
    [InlineData("Season 1.5 is coming, e.g. new maps and U.S. servers. Cannot wait to try them out!")]
    public async Task Keeps_ordinary_write_ups(string modelOutput)
        => Assert.Equal(modelOutput, (await Writer(new FakeChat(modelOutput)).WriteAsync(Article, 1, "Bot", "Server"))?.Text);

    [Fact]
    public async Task Strips_wrapping_quotes()
    {
        var writeUp = await Writer(new FakeChat("\"Hey squad, Season 1 drops on November 6 with new maps!\""))
            .WriteAsync(Article, 1, "Bot", "Server");

        Assert.Equal("Hey squad, Season 1 drops on November 6 with new maps!", writeUp?.Text);
    }

    [Fact]
    public async Task Discards_write_ups_that_break_the_word_filter()
        => Assert.Null(await Writer(new FakeChat("Season 1 is here and it is a badword of an update, squad!"))
            .WriteAsync(Article, 1, "Bot", "Server"));

    [Fact]
    public async Task Backs_off_after_a_failure()
    {
        var chat = new FakeChat("unused", fail: true);
        var writer = Writer(chat);

        Assert.Null(await writer.WriteAsync(Article, 1, "Bot", "Server"));
        Assert.Null(await writer.WriteAsync(Article, 1, "Bot", "Server"));
        Assert.Equal(1, chat.Calls); // the second article didn't wait on the model
    }
}
