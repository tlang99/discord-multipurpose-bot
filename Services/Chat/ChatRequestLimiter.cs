using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Services.Chat;

/// <summary>
/// Caps how many requests go to the model at once (<see cref="ChatOptions.MaxConcurrentRequests"/>), across everything
/// that uses it: chat replies, chat actions and news write-ups. A singleton, since the HttpClient-based chat service
/// itself is created per use.
/// </summary>
public sealed class ChatRequestLimiter
{
    private readonly SemaphoreSlim _semaphore;

    public ChatRequestLimiter(IOptions<ChatOptions> options)
    {
        _semaphore = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentRequests));
    }

    /// <summary>Waits for a free slot; dispose the result to give it back.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Slot(_semaphore);
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
