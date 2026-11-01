using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.News;
using ModBot.Services.Storage;

namespace ModBot.Hosting;

/// <summary>
/// Periodically checks every news subscription for articles that haven't been posted yet and posts them to the
/// subscription's channel, oldest first.
/// </summary>
public class NewsPollingService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly DiscordSocketClient _client;
    private readonly INewsSubscriptionStore _store;
    private readonly INewsFeedReader _feedReader;
    private readonly NewsOptions _options;
    private readonly ILogger<NewsPollingService> _logger;

    public NewsPollingService(
        DiscordSocketClient client,
        INewsSubscriptionStore store,
        INewsFeedReader feedReader,
        IOptions<NewsOptions> options,
        ILogger<NewsPollingService> logger)
    {
        _client = client;
        _store = store;
        _feedReader = feedReader;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.PollIntervalMinutes));

        try
        {
            await UpgradeLegacyFeedUrlsAsync();

            // Give the gateway connection a head start so the first check isn't wasted.
            await Task.Delay(StartupDelay, stoppingToken);

            using var timer = new PeriodicTimer(interval);
            do
            {
                await PollAllAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>Points subscriptions made from a built-in preset at the preset's current feed URL. Seen-article history
    /// is kept, so only articles the old feed missed get posted.</summary>
    /// <remarks>Never throws: an exception escaping ExecuteAsync would stop news polling for good, and by default the
    /// whole bot with it. If this fails, polling just carries on with the old URLs and it's retried next start.</remarks>
    private async Task UpgradeLegacyFeedUrlsAsync()
    {
        try
        {
            foreach (var subscription in await _store.GetAllAsync())
            {
                if (NewsPresets.UpgradeLegacyFeedUrl(subscription.FeedUrl) is not { } newUrl)
                {
                    continue;
                }

                await _store.UpdateAsync(subscription.GuildId, subscription.Name, s => s.FeedUrl = newUrl);
                _logger.LogInformation("Updated the feed URL for '{Name}' in guild {GuildId} to the date-sorted version.",
                    subscription.Name, subscription.GuildId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't update news subscriptions to the current feed URLs; will retry next start.");
        }
    }

    private async Task PollAllAsync(CancellationToken cancellationToken)
    {
        if (_client.ConnectionState != ConnectionState.Connected)
        {
            _logger.LogInformation("Skipping news check - not connected to Discord.");
            return;
        }

        var subscriptions = await _store.GetAllAsync();
        if (subscriptions.Count == 0)
        {
            _logger.LogInformation("Skipping news check - no feeds are being followed.");
            return;
        }

        foreach (var subscription in subscriptions)
        {
            try
            {
                await PollAsync(subscription, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "News check failed for '{Name}' in guild {GuildId}.",
                    subscription.Name, subscription.GuildId);
            }
        }
    }

    private async Task PollAsync(NewsSubscription subscription, CancellationToken cancellationToken)
    {
        if (_client.GetChannel(subscription.ChannelId) is not ITextChannel channel)
        {
            _logger.LogWarning("News channel {ChannelId} for '{Name}' in guild {GuildId} no longer exists or isn't visible.",
                subscription.ChannelId, subscription.Name, subscription.GuildId);
            return;
        }

        var articles = await _feedReader.ReadAsync(subscription.FeedUrl, cancellationToken);
        var seen = subscription.SeenItemIds.ToHashSet();
        // DistinctBy: a feed that lists the same article twice would otherwise get it posted twice in one check.
        var unseen = articles.Where(a => !seen.Contains(a.Id)).DistinctBy(a => a.Id).ToList();
        if (unseen.Count == 0)
        {
            _logger.LogInformation("News check for '{Name}' in guild {GuildId}: nothing posted - no new articles ({Total} in feed, all already seen).",
                subscription.Name, subscription.GuildId, articles.Count);
            return;
        }

        // Filtered-out articles are marked seen too, so loosening the filter later doesn't dump a backlog of old
        // articles. Same for articles past the cap - better to skip a few than flood the channel.
        var matching = unseen.Where(subscription.Matches).ToList();
        var toPost = matching.Take(Math.Max(1, _options.MaxPostsPerPoll)).ToList();
        var skipped = unseen.Except(toPost).Select(a => a.Id).ToList();
        if (skipped.Count > 0)
        {
            await _store.MarkSeenAsync(subscription.GuildId, subscription.Name, skipped);
        }

        var filteredCount = unseen.Count - matching.Count;
        var cappedCount = matching.Count - toPost.Count;

        if (toPost.Count == 0)
        {
            _logger.LogInformation("News check for '{Name}' in guild {GuildId}: nothing posted - all {Count} new article(s) filtered out by keywords.",
                subscription.Name, subscription.GuildId, unseen.Count);
            return;
        }

        toPost.Reverse(); // oldest first, so the newest ends up at the bottom of the channel
        foreach (var article in toPost)
        {
            await channel.SendMessageAsync(embed: NewsEmbedFactory.Build(article, subscription.Name));

            // Mark one at a time so a failure mid-way (e.g. permissions revoked) retries only what wasn't sent.
            await _store.MarkSeenAsync(subscription.GuildId, subscription.Name, [article.Id]);
        }

        _logger.LogInformation("News check for '{Name}' in guild {GuildId}: posted {Count} article(s) to #{Channel} " +
            "({Filtered} filtered out, {Capped} skipped over the {Max}-per-check cap).",
            subscription.Name, subscription.GuildId, toPost.Count, channel.Name,
            filteredCount, cappedCount, Math.Max(1, _options.MaxPostsPerPoll));
    }
}
