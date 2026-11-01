using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Reminders;
using ModBot.Services.Storage;

namespace ModBot.Hosting;

/// <summary>
/// Posts reminders when they're due, in the channel they were set in (as a reply to the chat message that asked, if
/// any), pinging only the person. Reminders that came due while the bot was offline are delivered once it's ready
/// again, marked as late. If the channel can't be used, it tries a DM instead; if that fails too, it retries with a
/// growing gap before giving up.
/// </summary>
public class ReminderDeliveryService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>A reminder more than this late gets a note saying so (the bot was offline when it came due).</summary>
    private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    /// <summary>Retry gaps double from 2 minutes up to this, so a Discord outage of a few minutes doesn't drop
    /// anything.</summary>
    private static readonly TimeSpan MaxRetryGap = TimeSpan.FromHours(1);

    /// <summary>Gives up after this many failed attempts, or once a reminder is this overdue, whichever comes first
    /// (e.g. its channel was deleted and the person's DMs are closed).</summary>
    private const int MaxAttempts = 10;

    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    /// <summary>How long a delivery holds its claim: longer than a send (channel plus DM fallback) takes.</summary>
    private static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(2);

    private readonly DiscordSocketClient _client;
    private readonly IReminderStore _store;
    private readonly ILogger<ReminderDeliveryService> _logger;

    /// <summary>Set once the client has its guilds and channels. Straight after connecting, channels can look missing,
    /// which would send overdue reminders to DMs instead of where they were set.</summary>
    private volatile bool _ready;

    public ReminderDeliveryService(DiscordSocketClient client, IReminderStore store,
        ILogger<ReminderDeliveryService> logger)
    {
        _client = client;
        _store = store;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_ready || _client.ConnectionState != ConnectionState.Connected)
                {
                    continue;
                }

                try
                {
                    foreach (var reminder in await _store.GetDueAsync(DateTimeOffset.UtcNow))
                    {
                        await DeliverAsync(reminder);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never let one bad check stop reminders for good.
                    _logger.LogError(ex, "Reminder check failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private async Task DeliverAsync(Reminder reminder)
    {
        // Claim it in one step with the existence check: a cancel from now on is told it's too late, and one that got
        // in first means this fails and nothing is sent. The claim expires, so a crash mid-send means a retry.
        var now = DateTimeOffset.UtcNow;
        if (!await _store.TryClaimAsync(reminder.Id, now, now + ClaimFor))
        {
            return;
        }

        // Only a first attempt that's late means the bot was offline; a retried one was late because sending failed.
        var late = now - reminder.DueAt <= LateAfter ? ""
            : reminder.Attempts == 0
                ? $"\n-# This was due {ReminderService.Relative(reminder.DueAt)} - I was offline then."
                : $"\n-# This was due {ReminderService.Relative(reminder.DueAt)}.";

        // Send first, remove after. If the bot stops in between, the reminder is sent again on restart (marked late),
        // which beats losing it.
        if (await TrySendInChannelAsync(reminder, late) || await TrySendDmAsync(reminder, late))
        {
            await _store.RemoveAsync(reminder.Id);
            return;
        }

        var attempts = reminder.Attempts + 1;
        if (attempts >= MaxAttempts || now - reminder.DueAt > GiveUpAfter)
        {
            await _store.RemoveAsync(reminder.Id);
            _logger.LogWarning("Dropping reminder {Id} for user {UserId} after {Attempts} failed deliveries.",
                reminder.Id, reminder.UserId, attempts);
            return;
        }

        var gap = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts), MaxRetryGap.TotalMinutes));
        await _store.UpdateAsync(reminder.Id, r =>
        {
            r.Attempts = attempts;
            r.NextAttemptAt = now + gap;
            r.ClaimedUntil = null; // cancellable again while it waits
        });
        _logger.LogInformation("Couldn't deliver reminder {Id}; trying again in {Minutes} minutes (attempt {Attempt}).",
            reminder.Id, (int)gap.TotalMinutes, attempts);
    }

    private async Task<bool> TrySendInChannelAsync(Reminder reminder, string late)
    {
        if (_client.GetChannel(reminder.ChannelId) is not IMessageChannel channel)
        {
            return false;
        }

        try
        {
            await channel.SendMessageAsync(
                $"⏰ <@{reminder.UserId}>, you asked me to remind you: {reminder.Text}{late}",
                allowedMentions: new AllowedMentions(AllowedMentionTypes.None) { UserIds = [reminder.UserId] },
                messageReference: reminder.SourceMessageId is { } source
                    ? new MessageReference(source, reminder.ChannelId, reminder.GuildId, failIfNotExists: false)
                    : null,
                // The text is whatever the person typed; don't let a link in it unfurl into a preview.
                flags: MessageFlags.SuppressEmbeds);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't post reminder {Id} in channel {ChannelId}; trying a DM.",
                reminder.Id, reminder.ChannelId);
            return false;
        }
    }

    private async Task<bool> TrySendDmAsync(Reminder reminder, string late)
    {
        try
        {
            if (await _client.GetUserAsync(reminder.UserId) is not { } user)
            {
                return false;
            }

            var server = _client.GetGuild(reminder.GuildId)?.Name ?? "a server";
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync($"⏰ Reminder you set in **{Format.Sanitize(server)}** (I couldn't post it there): " +
                                      $"{reminder.Text}{late}", allowedMentions: AllowedMentions.None,
                flags: MessageFlags.SuppressEmbeds);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't DM reminder {Id} to user {UserId}.", reminder.Id, reminder.UserId);
            return false;
        }
    }
}
