using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonWarningStore : IWarningStore
{
    private readonly JsonFileRepository<List<Warning>> _repository;

    public JsonWarningStore()
    {
        _repository = new JsonFileRepository<List<Warning>>(Path.Combine("data", "warnings.json"));
    }

    public async Task<Warning> AddWarningAsync(ulong guildId, ulong userId, ulong moderatorId, string reason)
    {
        var warning = new Warning
        {
            GuildId = guildId,
            UserId = userId,
            ModeratorId = moderatorId,
            Reason = reason
        };

        var key = Key(guildId, userId);
        await _repository.MutateAsync(data =>
        {
            if (!data.TryGetValue(key, out var list))
            {
                list = new List<Warning>();
                data[key] = list;
            }

            list.Add(warning);
        });

        return warning;
    }

    public async Task<IReadOnlyList<Warning>> GetWarningsAsync(ulong guildId, ulong userId)
    {
        var list = await _repository.GetAsync(Key(guildId, userId));
        return list ?? new List<Warning>();
    }

    public async Task<int> ClearWarningsAsync(ulong guildId, ulong userId)
    {
        var key = Key(guildId, userId);
        var removedCount = 0;

        await _repository.MutateAsync(data =>
        {
            if (data.TryGetValue(key, out var list))
            {
                removedCount = list.Count;
                data.Remove(key);
            }
        });

        return removedCount;
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";
}
