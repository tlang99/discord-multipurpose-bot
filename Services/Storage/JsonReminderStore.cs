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

    public Task<ReminderAddResult> TryAddAsync(Reminder reminder, int maxPerUser)
        => _repository.MutateAsync(data =>
        {
            if (data.ContainsKey(reminder.Id))
            {
                return ReminderAddResult.IdTaken;
            }

            if (data.Values.Count(r => r.GuildId == reminder.GuildId && r.UserId == reminder.UserId) >= maxPerUser)
            {
                return ReminderAddResult.LimitReached;
            }

            data[reminder.Id] = reminder;
            return ReminderAddResult.Added;
        });

    public Task<IReadOnlyList<Reminder>> GetForUserAsync(ulong guildId, ulong userId)
        => GetSortedAsync(r => r.GuildId == guildId && r.UserId == userId);

    public Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset now)
        => GetSortedAsync(r => r.DueAt <= now && (r.NextAttemptAt is null || r.NextAttemptAt <= now) &&
                               !IsClaimed(r, now));

    public Task<bool> TryClaimAsync(string id, DateTimeOffset now, DateTimeOffset claimUntil)
        => _repository.MutateAsync(data =>
        {
            if (!data.TryGetValue(id, out var reminder) || IsClaimed(reminder, now))
            {
                return false;
            }

            reminder.ClaimedUntil = claimUntil;
            return true;
        });

    public Task<(ReminderCancelResult Result, Reminder? Reminder)> TryCancelAsync(string id, ulong guildId,
        ulong userId, DateTimeOffset now)
        => _repository.MutateAsync<(ReminderCancelResult, Reminder?)>(data =>
        {
            if (!data.TryGetValue(id, out var reminder) || reminder.GuildId != guildId || reminder.UserId != userId)
            {
                return (ReminderCancelResult.NotFound, null);
            }

            if (IsClaimed(reminder, now))
            {
                return (ReminderCancelResult.InFlight, reminder);
            }

            data.Remove(id);
            return (ReminderCancelResult.Cancelled, reminder);
        });

    public Task<bool> UpdateAsync(string id, Action<Reminder> update) => _repository.UpdateAsync(id, update);

    public Task<bool> RemoveAsync(string id) => _repository.RemoveAsync(id);

    /// <summary>A delivery holds an unexpired claim on it.</summary>
    private static bool IsClaimed(Reminder reminder, DateTimeOffset now)
        => reminder.ClaimedUntil is { } until && until > now;

    private async Task<IReadOnlyList<Reminder>> GetSortedAsync(Func<Reminder, bool> filter)
        => (await _repository.GetAllAsync()).Where(filter).OrderBy(r => r.DueAt).ToList();
}
