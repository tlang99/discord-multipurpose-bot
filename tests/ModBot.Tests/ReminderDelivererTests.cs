using Microsoft.Extensions.Logging.Abstractions;
using ModBot.Models;
using ModBot.Services.Reminders;

namespace ModBot.Tests;

public sealed class ReminderDelivererTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private readonly TempReminderStore _temp = new();
    private readonly FakeSender _sender = new();
    private readonly ReminderDeliverer _deliverer;

    public ReminderDelivererTests()
    {
        _deliverer = new ReminderDeliverer(_temp.Store, _sender, NullLogger<ReminderDeliverer>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<Reminder> AddAsync(DateTimeOffset dueAt, int attempts = 0)
    {
        var reminder = new Reminder
        {
            Id = "abcd", GuildId = 1, ChannelId = 10, UserId = 100, Text = "get a beer", DueAt = dueAt,
            Attempts = attempts,
        };
        await _temp.Store.TryAddAsync(reminder, maxPerUser: 25);
        return reminder;
    }

    private async Task<Reminder?> StoredAsync() => (await _temp.Store.GetForUserAsync(1, 100)).SingleOrDefault();

    [Fact]
    public async Task Posts_in_the_channel_and_removes_it()
    {
        await AddAsync(Now.AddSeconds(-10));

        await _deliverer.DeliverDueAsync(Now);

        Assert.Equal(1, _sender.ChannelSends);
        Assert.Equal(0, _sender.DmSends);
        Assert.Equal("", _sender.LastLateNote);
        Assert.Null(await StoredAsync());
    }

    [Fact]
    public async Task Falls_back_to_a_dm_when_the_channel_fails()
    {
        _sender.ChannelWorks = false;
        await AddAsync(Now.AddSeconds(-10));

        await _deliverer.DeliverDueAsync(Now);

        Assert.Equal(1, _sender.DmSends);
        Assert.Null(await StoredAsync());
    }

    [Fact]
    public async Task Not_due_yet_is_left_alone()
    {
        await AddAsync(Now.AddMinutes(5));

        await _deliverer.DeliverDueAsync(Now);

        Assert.Equal(0, _sender.ChannelSends);
        Assert.NotNull(await StoredAsync());
    }

    [Fact]
    public async Task A_failed_send_backs_off_and_stays_cancellable()
    {
        _sender.ChannelWorks = _sender.DmWorks = false;
        await AddAsync(Now.AddSeconds(-10));

        await _deliverer.DeliverDueAsync(Now);

        var stored = await StoredAsync();
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(Now + ReminderDeliverer.RetryGap(1), stored.NextAttemptAt);
        Assert.Null(stored.ClaimedUntil);

        // Waiting out the gap: not retried before it's over.
        await _deliverer.DeliverDueAsync(Now.AddMinutes(1));
        Assert.Equal(1, _sender.ChannelSends);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt()
    {
        _sender.ChannelWorks = _sender.DmWorks = false;
        await AddAsync(Now.AddMinutes(-30), attempts: ReminderDeliverer.MaxAttempts - 1);

        await _deliverer.DeliverDueAsync(Now);

        Assert.Null(await StoredAsync());
    }

    [Fact]
    public async Task Gives_up_once_a_day_overdue()
    {
        _sender.ChannelWorks = _sender.DmWorks = false;
        await AddAsync(Now - ReminderDeliverer.GiveUpAfter - TimeSpan.FromMinutes(1));

        await _deliverer.DeliverDueAsync(Now);

        Assert.Null(await StoredAsync());
    }

    [Fact]
    public async Task A_claimed_reminder_isnt_sent_twice()
    {
        var reminder = await AddAsync(Now.AddSeconds(-10));
        Assert.True(await _temp.Store.TryClaimAsync(reminder.Id, Now, Now + ReminderDeliverer.ClaimFor));

        await _deliverer.DeliverDueAsync(Now);

        Assert.Equal(0, _sender.ChannelSends);
    }

    [Fact]
    public async Task Late_first_attempt_says_the_bot_was_offline()
    {
        await AddAsync(Now.AddMinutes(-10));

        await _deliverer.DeliverDueAsync(Now);

        Assert.Contains("I was offline", _sender.LastLateNote);
    }

    [Fact]
    public void Late_retry_doesnt_claim_the_bot_was_offline()
    {
        var note = ReminderDeliverer.LateNote(new Reminder { DueAt = Now.AddMinutes(-10), Attempts = 2 }, Now);

        Assert.Contains("This was due", note);
        Assert.DoesNotContain("offline", note);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(9, 60)] // capped at an hour
    public void Retry_gaps_double_up_to_an_hour(int attempts, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), ReminderDeliverer.RetryGap(attempts));

    private sealed class FakeSender : IReminderSender
    {
        public bool ChannelWorks { get; set; } = true;
        public bool DmWorks { get; set; } = true;
        public int ChannelSends { get; private set; }
        public int DmSends { get; private set; }
        public string? LastLateNote { get; private set; }

        public Task<bool> TrySendInChannelAsync(Reminder reminder, string lateNote)
        {
            ChannelSends++;
            LastLateNote = lateNote;
            return Task.FromResult(ChannelWorks);
        }

        public Task<bool> TrySendDmAsync(Reminder reminder, string lateNote)
        {
            DmSends++;
            LastLateNote = lateNote;
            return Task.FromResult(DmWorks);
        }
    }
}
