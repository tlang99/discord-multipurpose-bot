using System.Text.RegularExpressions;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.News;
using ModBot.Services.Storage;

namespace ModBot.Modules.News;

[Group("news", "Automatically post gaming news to a channel.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
public partial class NewsModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxSubscriptionsPerGuild = 10;

    private readonly INewsSubscriptionStore _store;
    private readonly INewsFeedReader _feedReader;
    private readonly ILogger<NewsModule> _logger;

    public NewsModule(INewsSubscriptionStore store, INewsFeedReader feedReader, ILogger<NewsModule> logger)
    {
        _store = store;
        _feedReader = feedReader;
        _logger = logger;
    }

    [SlashCommand("follow-game", "Posts news about a game to a channel as it's published.")]
    public async Task FollowGameAsync(
        [Summary(description: "Which game to follow")] NewsPreset game,
        [Summary(description: "Channel to post news in")] ITextChannel channel)
    {
        var preset = NewsPresets.All[game];
        await FollowAsync(preset.Name, preset.FeedUrl, channel, preset.DisplayName, preset);
    }

    [SlashCommand("follow-feed", "Posts articles from any RSS/Atom feed to a channel as they're published.")]
    public async Task FollowFeedAsync(
        [Summary(description: "Short name for this feed, e.g. \"cod-blog\" (letters, numbers, dashes)")] string name,
        [Summary(description: "The feed's https:// URL")] string url,
        [Summary(description: "Channel to post articles in")] ITextChannel channel)
    {
        name = name.Trim().ToLowerInvariant();
        if (!NameRegex().IsMatch(name))
        {
            await RespondAsync("Names must be 1-32 characters: lowercase letters, numbers, and dashes.", ephemeral: true);
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            await RespondAsync("That doesn't look like a valid `https://` feed URL.", ephemeral: true);
            return;
        }

        await FollowAsync(name, uri.ToString(), channel, name, preset: null);
    }

    public enum FilterKind
    {
        [ChoiceDisplay("Only post articles mentioning this")]
        Include,

        [ChoiceDisplay("Skip articles with this in the headline")]
        Exclude,
    }

    [SlashCommand("filter-add", "Adds a keyword to a feed's filter.")]
    public async Task FilterAddAsync(
        [Summary(description: "Feed name, as shown by /news list")] string name,
        [Summary(description: "Word or phrase, e.g. \"battle pass\"")] string keyword,
        [Summary(description: "What the keyword does")] FilterKind type)
    {
        keyword = keyword.Trim().ToLowerInvariant();
        if (keyword.Length is 0 or > 50)
        {
            await RespondAsync("Keywords must be 1-50 characters.", ephemeral: true);
            return;
        }

        var subscription = await _store.GetAsync(Context.Guild.Id, name.Trim());
        if (subscription is null)
        {
            await RespondAsync("No followed feed has that name - check `/news list`.", ephemeral: true);
            return;
        }

        var list = type == FilterKind.Include ? subscription.IncludeKeywords : subscription.ExcludeKeywords;
        if (list.Contains(keyword))
        {
            await RespondAsync($"`{keyword}` is already on that list.", ephemeral: true);
            return;
        }

        if (list.Count >= 100)
        {
            await RespondAsync("That list is full (100 keywords).", ephemeral: true);
            return;
        }

        list.Add(keyword);
        await _store.SaveAsync(subscription);
        await RespondAsync(type == FilterKind.Include
            ? $"**{subscription.Name}** will now also post articles mentioning `{keyword}`."
            : $"**{subscription.Name}** will now skip articles with `{keyword}` in the headline.", ephemeral: true);
    }

    [SlashCommand("filter-remove", "Removes a keyword from a feed's filter.")]
    public async Task FilterRemoveAsync(
        [Summary(description: "Feed name, as shown by /news list")] string name,
        [Summary(description: "The keyword to remove")] string keyword)
    {
        keyword = keyword.Trim().ToLowerInvariant();
        var subscription = await _store.GetAsync(Context.Guild.Id, name.Trim());
        if (subscription is null)
        {
            await RespondAsync("No followed feed has that name - check `/news list`.", ephemeral: true);
            return;
        }

        var removed = subscription.IncludeKeywords.Remove(keyword) | subscription.ExcludeKeywords.Remove(keyword);
        if (!removed)
        {
            await RespondAsync($"`{keyword}` isn't in that feed's filter - check `/news filter-show`.", ephemeral: true);
            return;
        }

        await _store.SaveAsync(subscription);
        await RespondAsync($"Removed `{keyword}` from **{subscription.Name}**'s filter." +
            (subscription.IncludeKeywords.Count == 0 ? " (No include keywords left, so every article will be posted.)" : ""),
            ephemeral: true);
    }

    [SlashCommand("filter-show", "Shows which keywords a feed is filtered by.")]
    public async Task FilterShowAsync([Summary(description: "Feed name, as shown by /news list")] string name)
    {
        var subscription = await _store.GetAsync(Context.Guild.Id, name.Trim());
        if (subscription is null)
        {
            await RespondAsync("No followed feed has that name - check `/news list`.", ephemeral: true);
            return;
        }

        static string Format(List<string> words) => words.Count == 0 ? "*(none)*" : $"`{string.Join("`, `", words)}`";

        await RespondAsync(
            $"**{subscription.Name}** filter\n" +
            $"**Posts articles mentioning:** {(subscription.IncludeKeywords.Count == 0 ? "*(anything - no filter)*" : Format(subscription.IncludeKeywords))}\n" +
            $"**Skips headlines containing:** {Format(subscription.ExcludeKeywords)}",
            ephemeral: true);
    }

    [SlashCommand("unfollow", "Stops posting news from a followed feed.")]
    public async Task UnfollowAsync([Summary(description: "Feed name, as shown by /news list")] string name)
    {
        var removed = await _store.RemoveAsync(Context.Guild.Id, name.Trim());
        await RespondAsync(removed
            ? $"Stopped following **{name.Trim().ToLowerInvariant()}**."
            : "No followed feed has that name - check `/news list`.", ephemeral: true);
    }

    [SlashCommand("list", "Lists the news feeds this server follows.")]
    public async Task ListAsync()
    {
        var subscriptions = await _store.GetForGuildAsync(Context.Guild.Id);
        if (subscriptions.Count == 0)
        {
            await RespondAsync("This server isn't following any news yet. Try `/news follow-game`.", ephemeral: true);
            return;
        }

        var lines = subscriptions.Select(s => $"• **{s.Name}** → <#{s.ChannelId}>\n  <{s.FeedUrl}>");
        await RespondAsync(string.Join("\n", lines), ephemeral: true);
    }

    [SlashCommand("latest", "Shows the most recent articles from a followed feed right now.")]
    public async Task LatestAsync(
        [Summary(description: "Feed name, as shown by /news list")] string name,
        [Summary(description: "How many articles to show (1-5)"), MinValue(1), MaxValue(5)] int count = 3)
    {
        var subscription = await _store.GetAsync(Context.Guild.Id, name.Trim());
        if (subscription is null)
        {
            await RespondAsync("No followed feed has that name - check `/news list`.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        IReadOnlyList<NewsArticle> articles;
        try
        {
            articles = await _feedReader.ReadAsync(subscription.FeedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read feed {Url}.", subscription.FeedUrl);
            await FollowupAsync("Couldn't reach that feed right now - try again in a bit.", ephemeral: true);
            return;
        }

        articles = articles.Where(subscription.Matches).ToList();
        if (articles.Count == 0)
        {
            await FollowupAsync("Nothing in that feed matches its filter right now (see `/news filter-show`).", ephemeral: true);
            return;
        }

        var embeds = articles.Take(count).Select(a => NewsEmbedFactory.Build(a, subscription.Name)).ToArray();
        await FollowupAsync(embeds: embeds, ephemeral: true);
    }

    private async Task FollowAsync(string name, string feedUrl, ITextChannel channel, string displayName,
        NewsPresetInfo? preset)
    {
        // Without this, Manage Server alone would let someone make the bot post into channels they can't see
        // (e.g. staff-only ones). Only View Channel is required: news channels are usually read-only, so the person
        // setting it up may not be able to send messages there themselves.
        var userPermissions = (Context.User as IGuildUser)?.GetPermissions(channel) ?? default;
        if (!userPermissions.ViewChannel)
        {
            await RespondAsync($"You need to be able to see {channel.Mention} to have news posted there.",
                ephemeral: true);
            return;
        }

        var botPermissions = Context.Guild.CurrentUser.GetPermissions(channel);
        if (!botPermissions.ViewChannel || !botPermissions.SendMessages || !botPermissions.EmbedLinks)
        {
            await RespondAsync(
                $"I need **View Channel**, **Send Messages**, and **Embed Links** in {channel.Mention} to post news there.",
                ephemeral: true);
            return;
        }

        var existing = await _store.GetAsync(Context.Guild.Id, name);
        if (existing is null && (await _store.GetForGuildAsync(Context.Guild.Id)).Count >= MaxSubscriptionsPerGuild)
        {
            await RespondAsync($"This server already follows {MaxSubscriptionsPerGuild} feeds - `/news unfollow` one first.",
                ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        // Fetch once up front: validates the feed works, and lets us mark everything already in it as seen so the
        // channel isn't flooded with old articles on the next check.
        IReadOnlyList<NewsArticle> articles;
        try
        {
            articles = await _feedReader.ReadAsync(feedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read feed {Url} while subscribing.", feedUrl);
            await FollowupAsync(IsBlockedAddress(ex)
                    ? "That feed points to a private or internal network address, which isn't allowed."
                    : "Couldn't read that feed - make sure the URL points to an RSS or Atom feed.",
                ephemeral: true);
            return;
        }

        var subscription = existing ?? new NewsSubscription { GuildId = Context.Guild.Id, Name = name };
        subscription.FeedUrl = feedUrl;
        subscription.ChannelId = channel.Id;

        // Apply the preset's default filter to new subscriptions, and to existing ones that have no filter yet -
        // but never overwrite keywords a server has customised.
        if (preset is not null && subscription.IncludeKeywords.Count == 0 && subscription.ExcludeKeywords.Count == 0)
        {
            subscription.IncludeKeywords = preset.IncludeKeywords.ToList();
            subscription.ExcludeKeywords = preset.ExcludeKeywords.ToList();
        }

        await _store.SaveAsync(subscription);
        await _store.MarkSeenAsync(Context.Guild.Id, name, articles.Select(a => a.Id));

        var verb = existing is null ? "Now following" : "Updated";
        await FollowupAsync(
            $"{verb} **{displayName}** - new articles will be posted in {channel.Mention}. " +
            $"Use `/news latest {name}` to see what's out right now" +
            (subscription.IncludeKeywords.Count > 0 ? $", and `/news filter-show {name}` to see what gets posted." : "."),
            ephemeral: true);
    }

    /// <summary>HttpClient wraps connect-time failures, so look through the inner exceptions.</summary>
    private static bool IsBlockedAddress(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is BlockedAddressException)
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex NameRegex();
}
