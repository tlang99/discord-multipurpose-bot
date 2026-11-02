using ModBot.Models;

namespace ModBot.Services.Storage;

public interface IServerSettingsStore
{
    /// <summary>A copy of the server's settings (defaults if it has none saved). Changing it doesn't save anything.</summary>
    Task<ServerSettings> GetAsync(ulong guildId);

    /// <summary>Applies <paramref name="update"/> to the server's settings and saves, all under the store's lock, so
    /// two changes at once can't overwrite each other.</summary>
    Task UpdateAsync(ulong guildId, Action<ServerSettings> update);
}
