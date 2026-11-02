namespace ModBot.Common;

/// <summary>The loop behind the bot's timed background jobs (news checks, reminders, LFG expiry).</summary>
public static class PeriodicLoop
{
    /// <summary>Runs <paramref name="tick"/> every <paramref name="interval"/> until the bot shuts down, which ends
    /// the loop quietly rather than as an error.</summary>
    /// <param name="runFirstTickNow">Run once straight away instead of waiting a whole interval first.</param>
    public static async Task RunAsync(TimeSpan interval, Func<Task> tick, CancellationToken stoppingToken,
        bool runFirstTickNow = false)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            if (runFirstTickNow)
            {
                await tick();
            }

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await tick();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }
}
