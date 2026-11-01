using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonNewsSubscriptionStore : INewsSubscriptionStore
{
    /// <summary>Feeds typically return 20-100 items; keep comfortably more than that so items that drop off and
    /// reappear in a feed aren't re-posted.</summary>
    private const int MaxSeenItemsPerSubscription = 500;

    private readonly JsonFileRepository<NewsSubscription> _repository;

    public JsonNewsSubscriptionStore()
    {
        _repository = new JsonFileRepository<NewsSubscription>(Path.Combine("data", "news-subscriptions.json"));
    }

    public async Task<IReadOnlyList<NewsSubscription>> GetAllAsync()
        => (await _repository.GetAllAsync()).ToList();

    public async Task<IReadOnlyList<NewsSubscription>> GetForGuildAsync(ulong guildId)
        => (await _repository.GetAllAsync()).Where(s => s.GuildId == guildId).OrderBy(s => s.Name).ToList();

    public Task<NewsSubscription?> GetAsync(ulong guildId, string name)
        => _repository.GetAsync(Key(guildId, name));

    public Task UpsertAsync(ulong guildId, string name, Action<NewsSubscription, bool> update)
        => _repository.MutateAsync(data =>
        {
            var key = Key(guildId, name);
            var isNew = !data.TryGetValue(key, out var subscription);
            subscription ??= new NewsSubscription { GuildId = guildId, Name = name };

            update(subscription, isNew);
            TrimSeen(subscription);
            data[key] = subscription;
        });

    public async Task<bool> UpdateAsync(ulong guildId, string name, Action<NewsSubscription> update)
    {
        var found = false;
        await _repository.MutateAsync(data =>
        {
            if (data.TryGetValue(Key(guildId, name), out var subscription))
            {
                found = true;
                update(subscription);
                TrimSeen(subscription);
            }
        });
        return found;
    }

    public async Task<bool> RemoveAsync(ulong guildId, string name)
    {
        var removed = false;
        await _repository.MutateAsync(data => removed = data.Remove(Key(guildId, name)));
        return removed;
    }

    public Task MarkSeenAsync(ulong guildId, string name, IEnumerable<string> itemIds)
        => UpdateAsync(guildId, name, subscription => subscription.MarkSeen(itemIds));

    private static void TrimSeen(NewsSubscription subscription)
    {
        var overflow = subscription.SeenItemIds.Count - MaxSeenItemsPerSubscription;
        if (overflow > 0)
        {
            subscription.SeenItemIds.RemoveRange(0, overflow);
        }
    }

    private static string Key(ulong guildId, string name) => $"{guildId}:{name.ToLowerInvariant()}";
}
