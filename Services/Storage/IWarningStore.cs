using ModBot.Models;

namespace ModBot.Services.Storage;

public interface IWarningStore
{
    Task<Warning> AddWarningAsync(ulong guildId, ulong userId, ulong moderatorId, string reason);

    Task<IReadOnlyList<Warning>> GetWarningsAsync(ulong guildId, ulong userId);

    Task<int> ClearWarningsAsync(ulong guildId, ulong userId);
}
