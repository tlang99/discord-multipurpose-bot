using System.Collections.Concurrent;

namespace ModBot.Common;

/// <summary>One async lock per key (e.g. per server), so work for one server waits only on that server's.</summary>
/// <remarks>One small entry per key that has used it; never removed.</remarks>
public sealed class KeyedLock<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks = new();

    /// <summary>Runs <paramref name="work"/> holding <paramref name="key"/>'s lock.</summary>
    public async Task<T> RunAsync<T>(TKey key, Func<Task<T>> work)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RunAsync(TKey key, Func<Task> work)
        => RunAsync(key, async () =>
        {
            await work();
            return true;
        });
}
