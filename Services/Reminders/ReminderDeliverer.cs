using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Storage;

namespace ModBot.Services.Reminders;

/// <summary>
/// Delivers due reminders: in the channel they were set in, or by DM if that fails. If both fail, it retries with a
/// growing gap before giving up. Each reminder is claimed before sending, so a cancel can't report success for one
/// that's about to arrive. Reminders that came due while the bot was offline get a note saying they're late.
/// The timer lives in ReminderDeliveryService.
/// </summary>
public class ReminderDeliverer
{
    /// <summary>A reminder more than this late gets a note saying when it was due.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    /// <summary>Retry gaps double from 2 minutes up to this, so a Discord outage of a few minutes doesn't drop
    /// anything.</summary>
    public static readonly TimeSpan MaxRetryGap = TimeSpan.FromHours(1);

    /// <summary>Gives up after this many failed attempts, or once a reminder is <see cref="GiveUpAfter"/> overdue,
    /// whichever comes first (e.g. its channel was deleted and the person's DMs are closed).</summary>
    public const int MaxAttempts = 10;

    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    /// <summary>How long a delivery holds its claim: longer than a send (channel plus DM fallback) takes.</summary>
    public static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(2);

    private readonly IReminderStore _store;
    private readonly IReminderSender _sender;
    private readonly ILogger<ReminderDeliverer> _logger;

    public ReminderDeliverer(IReminderStore store, IReminderSender sender, ILogger<ReminderDeliverer> logger)
    {
        _store = store;
        _sender = sender;
        _logger = logger;
    }

    /// <summary>Delivers every reminder that's due at <paramref name="now"/>.</summary>
    public async Task DeliverDueAsync(DateTimeOffset now)
    {
        foreach (var reminder in await _store.GetDueAsync(now))
        {
            await DeliverAsync(reminder, now);
        }
    }

    private async Task DeliverAsync(Reminder reminder, DateTimeOffset now)
    {
        // Claim it in one step with the existence check. From then on a cancel is told it's too late, and if a cancel
        // got in first the claim fails and nothing is sent. The claim expires, so a crash mid-send means a retry.
        if (!await _store.TryClaimAsync(reminder.Id, now, now + ClaimFor))
        {
            return;
        }

        var late = LateNote(reminder, now);

        // Send first, remove after. If the bot stops in between, the reminder is sent again on restart (marked late),
        // which beats losing it.
        if (await _sender.TrySendInChannelAsync(reminder, late) || await _sender.TrySendDmAsync(reminder, late))
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

        var gap = RetryGap(attempts);
        await _store.UpdateAsync(reminder.Id, r =>
        {
            r.Attempts = attempts;
            r.NextAttemptAt = now + gap;
            r.ClaimedUntil = null; // cancellable again while it waits
        });
        _logger.LogInformation("Couldn't deliver reminder {Id}; trying again in {Minutes} minutes (attempt {Attempt}).",
            reminder.Id, (int)gap.TotalMinutes, attempts);
    }

    /// <summary>Empty unless the reminder is late. Only a late first attempt means the bot was offline; a retried
    /// one is late because sending failed.</summary>
    public static string LateNote(Reminder reminder, DateTimeOffset now)
        => now - reminder.DueAt <= LateAfter ? ""
            : reminder.Attempts == 0
                ? $"\n-# This was due {ReminderService.Relative(reminder.DueAt)} - I was offline then."
                : $"\n-# This was due {ReminderService.Relative(reminder.DueAt)}.";

    /// <summary>The wait after the given number of failed attempts: 2, 4, 8... minutes, capped at
    /// <see cref="MaxRetryGap"/>.</summary>
    public static TimeSpan RetryGap(int attempts)
        => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts), MaxRetryGap.TotalMinutes));
}
