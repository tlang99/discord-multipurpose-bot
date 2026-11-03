using ModBot.Models;

namespace ModBot.Services.Storage;

/// <summary>Reads return copies. Changes go through the update methods, which run under the store's lock so a
/// command editing a filter and the poller recording seen articles can't overwrite each other.</summary>
public interface INewsSubscriptionStore
{
    Task<IReadOnlyList<NewsSubscription>> GetAllAsync();

    Task<IReadOnlyList<NewsSubscription>> GetForGuildAsync(ulong guildId);

    Task<NewsSubscription?> GetAsync(ulong guildId, string name);

    /// <summary>Creates the subscription if it doesn't exist (the callback's <c>isNew</c> says which), applies
    /// <paramref name="update"/>, and saves.</summary>
    Task UpsertAsync(ulong guildId, string name, Action<NewsSubscription, bool> update);

    /// <summary>Applies <paramref name="update"/> and saves. Returns false (and does nothing) if there's no such
    /// subscription.</summary>
    Task<bool> UpdateAsync(ulong guildId, string name, Action<NewsSubscription> update);

    Task<bool> RemoveAsync(ulong guildId, string name);

    /// <summary>Sets <see cref="NewsSubscription.Silent"/> on every feed the guild follows, in one step under the
    /// store's lock, so a feed followed or unfollowed meanwhile can't make the result wrong. Returns how many feeds
    /// the guild has and how many of them actually changed.</summary>
    Task<(int Feeds, int Changed)> SetSilentForGuildAsync(ulong guildId, bool silent);

    /// <summary>Records articles as posted. No-op if the subscription was removed in the meantime.</summary>
    Task MarkSeenAsync(ulong guildId, string name, IEnumerable<string> itemIds);
}
