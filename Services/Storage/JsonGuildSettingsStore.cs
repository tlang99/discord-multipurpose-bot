using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonGuildSettingsStore : IGuildSettingsStore
{
    private readonly JsonFileRepository<GuildSettings> _repository;

    public JsonGuildSettingsStore()
    {
        _repository = new JsonFileRepository<GuildSettings>(Path.Combine("data", "guild-settings.json"));
    }

    public async Task<GuildSettings> GetAsync(ulong guildId)
    {
        var settings = await _repository.GetAsync(guildId.ToString());
        return settings ?? new GuildSettings { GuildId = guildId };
    }

    public Task UpdateAsync(ulong guildId, Action<GuildSettings> update)
        => _repository.MutateAsync(data =>
        {
            var key = guildId.ToString();
            if (!data.TryGetValue(key, out var settings))
            {
                settings = new GuildSettings { GuildId = guildId };
                data[key] = settings;
            }

            update(settings);
        });
}
