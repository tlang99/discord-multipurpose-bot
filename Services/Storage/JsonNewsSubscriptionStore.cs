using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonNewsSubscriptionStore : INewsSubscriptionStore
{
    /// <summary>Feeds typically return 20-100 items; keep comfortably more than that so items that drop off and
    /// reappear in a feed aren't re-posted.</summary>
    private const int MaxSeenItemsPerSubscription = 500;

    private readonly JsonFileRepository<NewsSubscription> _repository;

    public JsonNewsSubscriptionStore() : this(Path.Combine("data", "news-subscriptions.json"))
    {
    }

    /// <summary>For tests: a store backed by a file of your choosing.</summary>
    public JsonNewsSubscriptionStore(string filePath)
    {
        _repository = new JsonFileRepository<NewsSubscription>(filePath);
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

    public Task<bool> UpdateAsync(ulong guildId, string name, Action<NewsSubscription> update)
        => _repository.UpdateAsync(Key(guildId, name), subscription =>
        {
            update(subscription);
            TrimSeen(subscription);
        });

    public Task<bool> RemoveAsync(ulong guildId, string name) => _repository.RemoveAsync(Key(guildId, name));

    public Task<(int Feeds, int Changed)> SetSilentForGuildAsync(ulong guildId, bool silent)
        => _repository.MutateAsync(data =>
        {
            var feeds = data.Values.Where(s => s.GuildId == guildId).ToList();
            var changed = 0;
            foreach (var subscription in feeds.Where(s => s.Silent != silent))
            {
                subscription.Silent = silent;
                changed++;
            }

            return (feeds.Count, changed);
        });

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
