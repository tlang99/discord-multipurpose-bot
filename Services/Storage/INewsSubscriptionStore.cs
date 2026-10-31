using ModBot.Models;

namespace ModBot.Services.Storage;

public interface INewsSubscriptionStore
{
    Task<IReadOnlyList<NewsSubscription>> GetAllAsync();

    Task<IReadOnlyList<NewsSubscription>> GetForGuildAsync(ulong guildId);

    Task<NewsSubscription?> GetAsync(ulong guildId, string name);

    Task SaveAsync(NewsSubscription subscription);

    Task<bool> RemoveAsync(ulong guildId, string name);

    /// <summary>Records articles as posted. No-op if the subscription was removed in the meantime.</summary>
    Task MarkSeenAsync(ulong guildId, string name, IEnumerable<string> itemIds);
}
