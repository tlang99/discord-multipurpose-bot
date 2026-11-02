using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModBot.Common;
using ModBot.Services.Reminders;

namespace ModBot.Hosting;

/// <summary>
/// Checks for due reminders every 15 seconds while the bot is connected and ready, and hands them to
/// <see cref="ReminderDeliverer"/>. Reminders that came due while the bot was offline go out once it's ready again.
/// </summary>
public class ReminderDeliveryService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly DiscordSocketClient _client;
    private readonly ReminderDeliverer _deliverer;
    private readonly ILogger<ReminderDeliveryService> _logger;

    /// <summary>Set once the client has its guilds and channels. Straight after connecting, channels can look missing,
    /// which would send overdue reminders to DMs instead of where they were set.</summary>
    private volatile bool _ready;

    public ReminderDeliveryService(DiscordSocketClient client, ReminderDeliverer deliverer,
        ILogger<ReminderDeliveryService> logger)
    {
        _client = client;
        _deliverer = deliverer;
        _logger = logger;

        // Constructed before the bot logs in, so the first Ready isn't missed. Ready fires again after reconnects.
        _client.Ready += () =>
        {
            _ready = true;
            return Task.CompletedTask;
        };
        _client.Disconnected += _ =>
        {
            _ready = false;
            return Task.CompletedTask;
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => PeriodicLoop.RunAsync(CheckInterval, DeliverDueAsync, stoppingToken);

    private async Task DeliverDueAsync()
    {
        if (!_ready || _client.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _deliverer.DeliverDueAsync(DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let one bad check stop reminders for good.
            _logger.LogError(ex, "Reminder check failed.");
        }
    }
}
