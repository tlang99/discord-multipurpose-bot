using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonWarningStore : IWarningStore
{
    private readonly JsonFileRepository<List<Warning>> _repository =
        JsonFileRepository<List<Warning>>.InDataFolder("warnings.json");

    public async Task<Warning> AddWarningAsync(ulong guildId, ulong userId, ulong moderatorId, string reason)
    {
        var warning = new Warning
        {
            GuildId = guildId,
            UserId = userId,
            ModeratorId = moderatorId,
            Reason = reason
        };

        await _repository.MutateOrCreateAsync(Key(guildId, userId), () => [], list =>
        {
            list.Add(warning);
            return true;
        });
        return warning;
    }

    public async Task<IReadOnlyList<Warning>> GetWarningsAsync(ulong guildId, ulong userId)
        => await _repository.GetAsync(Key(guildId, userId)) ?? [];

    public Task<int> ClearWarningsAsync(ulong guildId, ulong userId)
        => _repository.MutateAsync(data => data.Remove(Key(guildId, userId), out var list) ? list.Count : 0);

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";
}
