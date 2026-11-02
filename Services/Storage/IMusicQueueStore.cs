using ModBot.Models;

namespace ModBot.Services.Storage;

public interface IMusicQueueStore
{
    /// <summary>The server's queue (empty if it has never had one).</summary>
    Task<MusicQueueState> GetAsync(ulong guildId);

    /// <summary>Every server's queue, e.g. to redraw their menus at startup.</summary>
    Task<IReadOnlyCollection<MusicQueueState>> GetAllAsync();

    /// <summary>Changes the server's queue in one locked step (so two changes at once can't undo each other) and
    /// returns what <paramref name="change"/> returned.</summary>
    Task<T> MutateAsync<T>(ulong guildId, Func<MusicQueueState, T> change);

    /// <summary>Changes the server's queue in one locked step.</summary>
    Task MutateAsync(ulong guildId, Action<MusicQueueState> change)
        => MutateAsync(guildId, state =>
        {
            change(state);
            return true;
        });
}
