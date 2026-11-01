using ModBot.Models;

namespace ModBot.Services.Storage;

public interface IGuildSettingsStore
{
    /// <summary>A copy of the guild's settings (defaults if it has none saved). Changing it doesn't save anything.</summary>
    Task<GuildSettings> GetAsync(ulong guildId);

    /// <summary>Applies <paramref name="update"/> to the guild's settings and saves, all under the store's lock, so two
    /// changes at once can't overwrite each other.</summary>
    Task UpdateAsync(ulong guildId, Action<GuildSettings> update);
}
