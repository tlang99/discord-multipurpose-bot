using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Reminders;
using ModBot.Services.Storage;

namespace ModBot.Tests;

public sealed class ReminderServiceTests : IDisposable
{
    private readonly TempReminderStore _temp = new();
    private readonly ReminderService _service;

    public ReminderServiceTests()
    {
        _service = new ReminderService(_temp.Store, new FakeGuildSettings(),
            Options.Create(new ReminderOptions { MaxPerUser = 3 }), NullLogger<ReminderService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Sets_a_reminder()
    {
        var result = await _service.CreateAsync(1, 10, 100, "in 2 hours", "start the raid", applyWordFilter: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal("start the raid", result.Reminder!.Text);
        Assert.Matches("^[a-z2-9]{4}$", result.Reminder.Id);
    }

    [Theory]
    [InlineData("whenever", "start the raid")] // no time it understands
    [InlineData("in 400 days", "start the raid")] // over MaxDays
    [InlineData("10m", "   ")] // nothing to remind about
    public async Task Rejects_bad_requests(string when, string what)
        => Assert.False((await _service.CreateAsync(1, 10, 100, when, what, true)).Success);

    [Fact]
    public async Task Rejects_less_than_a_minute_away()
        => Assert.False((await _service.CreateAtAsync(1, 10, 100, DateTimeOffset.UtcNow.AddSeconds(10), "x", true)).Success);

    [Fact]
    public async Task Applies_the_word_filter_unless_exempt()
    {
        Assert.False((await _service.CreateAsync(1, 10, 100, "10m", "say badword", applyWordFilter: true)).Success);
        Assert.True((await _service.CreateAsync(1, 10, 100, "10m", "say badword", applyWordFilter: false)).Success);
    }

    [Fact]
    public async Task Caps_reminders_per_person_even_when_requests_arrive_together()
    {
        // Ten at once with a cap of three: the cap is checked in the same step as the add, so exactly three get in.
        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => _service.CreateAsync(1, 10, 100, $"{i + 1}h", $"reminder {i}", true)));

        Assert.Equal(3, results.Count(r => r.Success));
        Assert.Equal(3, (await _service.ListAsync(1, 100)).Count);

        // The cap is per person.
        Assert.True((await _service.CreateAsync(1, 10, 200, "1h", "another person", true)).Success);
    }

    [Fact]
    public async Task Lists_soonest_first()
    {
        await _service.CreateAsync(1, 10, 100, "3h", "third", true);
        await _service.CreateAsync(1, 10, 100, "1h", "first", true);
        await _service.CreateAsync(1, 10, 100, "2h", "second", true);

        Assert.Equal(["first", "second", "third"], (await _service.ListAsync(1, 100)).Select(r => r.Text));
    }

    [Fact]
    public async Task Only_the_owner_can_cancel_and_case_doesnt_matter()
    {
        var reminder = (await _service.CreateAsync(1, 10, 100, "1h", "mine", true)).Reminder!;

        Assert.False((await _service.CancelAsync(1, 999, reminder.Id)).Success);
        Assert.True((await _service.CancelAsync(1, 100, reminder.Id.ToUpperInvariant())).Success);
        Assert.Empty(await _service.ListAsync(1, 100));
    }

    [Fact]
    public async Task Survives_a_reload()
    {
        await _service.CreateAsync(1, 10, 100, "1h", "persisted", true);

        var reloaded = await new JsonReminderStore(_temp.FilePath).GetForUserAsync(1, 100);
        Assert.Equal("persisted", Assert.Single(reloaded).Text);
    }

    [Fact]
    public async Task Due_skips_reminders_waiting_out_a_retry()
    {
        var reminder = (await _service.CreateAsync(1, 10, 100, "1h", "retry me", true)).Reminder!;
        var afterDue = reminder.DueAt.AddMinutes(1);

        Assert.Single(await _temp.Store.GetDueAsync(afterDue));
        await _temp.Store.UpdateAsync(reminder.Id, r => r.NextAttemptAt = afterDue.AddMinutes(5));
        Assert.Empty(await _temp.Store.GetDueAsync(afterDue));
        Assert.Single(await _temp.Store.GetDueAsync(afterDue.AddMinutes(6)));
    }
}
