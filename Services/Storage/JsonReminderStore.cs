using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonReminderStore : IReminderStore
{
    private readonly JsonFileRepository<Reminder> _repository;

    public JsonReminderStore() : this(Path.Combine("data", "reminders.json"))
    {
    }

    /// <summary>For tests: a store backed by a file of your choosing.</summary>
    public JsonReminderStore(string filePath)
    {
        _repository = new JsonFileRepository<Reminder>(filePath);
    }

    public async Task<ReminderAddResult> TryAddAsync(Reminder reminder, int maxPerUser)
    {
        var result = ReminderAddResult.Added;
        await _repository.MutateAsync(data =>
        {
            if (data.ContainsKey(reminder.Id))
            {
                result = ReminderAddResult.IdTaken;
            }
            else if (data.Values.Count(r => r.GuildId == reminder.GuildId && r.UserId == reminder.UserId) >= maxPerUser)
            {
                result = ReminderAddResult.LimitReached;
            }
            else
            {
                data[reminder.Id] = reminder;
            }
        });
        return result;
    }

    public async Task<IReadOnlyList<Reminder>> GetForUserAsync(ulong guildId, ulong userId)
        => (await _repository.GetAllAsync())
            .Where(r => r.GuildId == guildId && r.UserId == userId)
            .OrderBy(r => r.DueAt)
            .ToList();

    public async Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset now)
        => (await _repository.GetAllAsync())
            .Where(r => r.DueAt <= now && (r.NextAttemptAt is null || r.NextAttemptAt <= now) &&
                        (r.ClaimedUntil is null || r.ClaimedUntil <= now))
            .OrderBy(r => r.DueAt)
            .ToList();

    public async Task<bool> TryClaimAsync(string id, DateTimeOffset now, DateTimeOffset claimUntil)
    {
        var claimed = false;
        await _repository.MutateAsync(data =>
        {
            if (data.TryGetValue(id, out var reminder) && (reminder.ClaimedUntil is null || reminder.ClaimedUntil <= now))
            {
                reminder.ClaimedUntil = claimUntil;
                claimed = true;
            }
        });
        return claimed;
    }

    public async Task<(ReminderCancelResult Result, Reminder? Reminder)> TryCancelAsync(string id, ulong guildId,
        ulong userId, DateTimeOffset now)
    {
        (ReminderCancelResult, Reminder?) result = (ReminderCancelResult.NotFound, null);
        await _repository.MutateAsync(data =>
        {
            if (!data.TryGetValue(id, out var reminder) || reminder.GuildId != guildId || reminder.UserId != userId)
            {
                return;
            }

            if (reminder.ClaimedUntil is { } until && until > now)
            {
                result = (ReminderCancelResult.InFlight, reminder);
                return;
            }

            data.Remove(id);
            result = (ReminderCancelResult.Cancelled, reminder);
        });
        return result;
    }

    public async Task<bool> UpdateAsync(string id, Action<Reminder> update)
    {
        var found = false;
        await _repository.MutateAsync(data =>
        {
            if (data.TryGetValue(id, out var reminder))
            {
                found = true;
                update(reminder);
            }
        });
        return found;
    }

    public async Task<bool> RemoveAsync(string id)
    {
        var removed = false;
        await _repository.MutateAsync(data => removed = data.Remove(id));
        return removed;
    }
}
