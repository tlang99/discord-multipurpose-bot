using Microsoft.Extensions.Logging;

namespace ModBot.Common;

/// <summary>Fire-and-forget work that must never take anything down with it when it fails.</summary>
public static class Background
{
    /// <summary>Runs <paramref name="work"/> on the thread pool without waiting for it, logging anything it throws as
    /// <paramref name="failure"/> (a log message template; <paramref name="args"/> fill it in).</summary>
    /// <remarks>The compiler can't check that the template's placeholders match <paramref name="args"/>, so keep
    /// the two side by side at the call site. A mismatch only garbles the log line; it never throws.</remarks>
    public static void Run(ILogger logger, Func<Task> work, LogLevel level, string failure, params object?[] args)
        => _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
#pragma warning disable CA2254 // The template is always a constant at the call site.
                logger.Log(level, ex, failure, args);
#pragma warning restore CA2254
            }
        });
}
