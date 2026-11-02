using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonServerSettingsStore : IServerSettingsStore
{
    private readonly JsonFileRepository<ServerSettings> _repository;

    public JsonServerSettingsStore() : this(Path.Combine("data", "server-settings.json"))
    {
    }

    /// <summary>For tests: a store backed by a file of your choosing.</summary>
    public JsonServerSettingsStore(string filePath)
    {
        _repository = new JsonFileRepository<ServerSettings>(filePath);
    }

    public async Task<ServerSettings> GetAsync(ulong guildId)
        => await _repository.GetAsync(guildId.ToString()) ?? new ServerSettings { GuildId = guildId };

    public Task UpdateAsync(ulong guildId, Action<ServerSettings> update)
        => _repository.MutateOrCreateAsync(guildId.ToString(), () => new ServerSettings { GuildId = guildId },
            settings =>
            {
                update(settings);
                return true;
            });
}
