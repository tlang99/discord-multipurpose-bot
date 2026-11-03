using System.Text.Json;
using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using ModBot.Models;
using ModBot.Services.News;
using ModBot.Services.Storage;

namespace ModBot.Tests;

public sealed class NewsNotificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonNewsSubscriptionStore _store;
    private readonly NewsNotifications _notifications;

    public NewsNotificationTests()
    {
        _store = new JsonNewsSubscriptionStore(Path.Combine(_directory, "news.json"));
        _notifications = new NewsNotifications(_store, NullLogger<NewsNotifications>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Feeds_saved_before_the_setting_existed_load_as_silent()
    {
        var old = JsonSerializer.Deserialize<NewsSubscription>("""{"GuildId":1,"Name":"mw4","ChannelId":2}""")!;

        Assert.True(old.Silent);
    }

    [Fact]
    public async Task Muting_every_feed_only_touches_the_asking_server()
    {
        await Follow(guildId: 1, "mw4", silent: false);
        await Follow(guildId: 1, "cod-blog", silent: false);
        await Follow(guildId: 2, "mw4", silent: false);

        await _notifications.SetAsync(1, feed: null, silent: true, Admin);

        Assert.All(await _store.GetForGuildAsync(1), s => Assert.True(s.Silent));
        Assert.False((await _store.GetAsync(2, "mw4"))!.Silent);
    }

    [Fact]
    public async Task One_feed_can_notify_while_the_others_stay_silent()
    {
        await Follow(guildId: 1, "mw4", silent: true);
        await Follow(guildId: 1, "patches", silent: true);

        var reply = await _notifications.SetAsync(1, "Patches", silent: false, Admin);

        Assert.Contains("notifies people again", reply);
        Assert.False((await _store.GetAsync(1, "patches"))!.Silent);
        Assert.True((await _store.GetAsync(1, "mw4"))!.Silent);
    }

    [Fact]
    public async Task An_unknown_feed_says_so()
        => Assert.Contains("No followed feed", await _notifications.SetAsync(1, "nope", silent: true, Admin));

    [Theory]
    [InlineData("mute news notifications", null, true)]
    [InlineData("unmute the news", null, false)]
    [InlineData("turn off news notifications for this server", null, true)]
    [InlineData("turn news notifications on", null, false)]
    [InlineData("make the mw4 feed silent", "mw4", true)]
    [InlineData("make the mw4 feed notify", "mw4", false)]
    [InlineData("can you mute the news feeds please", null, true)]
    [InlineData("make the news feed quiet", null, true)] // "news feed" is the news, not a feed called "news"
    public void Reads_chat_requests(string message, string? feed, bool silent)
        => Assert.Equal(new NewsNotifyRequest(feed, silent), NewsNotifications.Read(message));

    [Theory]
    [InlineData("the news is so loud")]
    [InlineData("how do I mute the news?")]
    [InlineData("mute the music")]
    [InlineData("turn off the music")]
    public void Ignores_everything_else(string message) => Assert.Null(NewsNotifications.Read(message));

    [Fact]
    public async Task A_named_feed_the_server_doesnt_follow_is_not_a_request()
    {
        await Follow(guildId: 1, "mw4", silent: true);

        Assert.NotNull(await _notifications.ReadForGuildAsync(1, "make the mw4 feed notify"));
        Assert.NotNull(await _notifications.ReadForGuildAsync(1, "mute news notifications"));
        Assert.Null(await _notifications.ReadForGuildAsync(1, "mute the sports news")); // goes on to normal chat
        Assert.Null(await _notifications.ReadForGuildAsync(2, "make the mw4 feed notify")); // another server's feed
    }

    private static IUser Admin => DiscordFake.Create<IUser>(new() { ["get_Username"] = "mod", ["get_Id"] = 9UL });

    private Task Follow(ulong guildId, string name, bool silent)
        => _store.UpsertAsync(guildId, name, (s, _) =>
        {
            s.FeedUrl = "https://example.com/feed";
            s.ChannelId = 5;
            s.Silent = silent;
        });
}

public sealed class NewsBulkMuteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Muting_all_counts_only_what_changed()
    {
        var store = new JsonNewsSubscriptionStore(Path.Combine(_directory, "news.json"));
        foreach (var (name, silent) in new[] { ("a", true), ("b", false), ("c", false) })
        {
            await store.UpsertAsync(1, name, (s, _) => s.Silent = silent);
        }

        var notifications = new NewsNotifications(store, NullLogger<NewsNotifications>.Instance);
        var reply = await notifications.SetAsync(1, feed: null, silent: true,
            DiscordFake.Create<IUser>(new() { ["get_Username"] = "mod", ["get_Id"] = 9UL }));

        Assert.Equal((3, 0), await store.SetSilentForGuildAsync(1, silent: true)); // nothing left to change
        Assert.Contains("All 3 news feeds", reply);
        Assert.Contains("(2 changed)", reply);
    }
}
