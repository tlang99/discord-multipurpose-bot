using ModBot.Models;

namespace ModBot.Services.Storage;

public interface IGuildSettingsStore
{
    Task<GuildSettings> GetAsync(ulong guildId);

    Task SaveAsync(GuildSettings settings);
}
