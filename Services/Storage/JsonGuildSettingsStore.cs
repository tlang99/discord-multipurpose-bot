using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonGuildSettingsStore : IGuildSettingsStore
{
    private readonly JsonFileRepository<GuildSettings> _repository =
        JsonFileRepository<GuildSettings>.InDataFolder("guild-settings.json");

    public async Task<GuildSettings> GetAsync(ulong guildId)
        => await _repository.GetAsync(guildId.ToString()) ?? new GuildSettings { GuildId = guildId };

    public Task UpdateAsync(ulong guildId, Action<GuildSettings> update)
        => _repository.MutateOrCreateAsync(guildId.ToString(), () => new GuildSettings { GuildId = guildId }, settings =>
        {
            update(settings);
            return true;
        });
}
