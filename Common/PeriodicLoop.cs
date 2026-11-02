using Microsoft.Extensions.Logging;

namespace ModBot.Common;

/// <summary>The loop behind the bot's timed background jobs (news checks, reminders, LFG expiry).</summary>
public static class PeriodicLoop
{
    /// <summary>Runs <paramref name="tick"/> every <paramref name="interval"/> until the bot shuts down, which ends
    /// the loop quietly rather than as an error. A tick that throws is logged and the next one runs as usual: an
    /// exception escaping a background service would stop the job for good, and by default the whole bot with it.
    /// </summary>
    /// <param name="job">For the log, e.g. "Reminder check".</param>
    /// <param name="runFirstTickNow">Run once straight away instead of waiting a whole interval first.</param>
    public static async Task RunAsync(TimeSpan interval, Func<Task> tick, ILogger logger, string job,
        CancellationToken stoppingToken, bool runFirstTickNow = false)
    {
        async Task TickSafelyAsync()
        {
            try
            {
                await tick();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "{Job} failed; trying again next time.", job);
            }
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            if (runFirstTickNow)
            {
                await TickSafelyAsync();
            }

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickSafelyAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }
}
