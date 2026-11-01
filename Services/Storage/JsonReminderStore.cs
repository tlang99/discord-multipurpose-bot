using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonReminderStore : IReminderStore
{
    private readonly JsonFileRepository<Reminder> _repository;

    public JsonReminderStore()
    {
        _repository = new JsonFileRepository<Reminder>(Path.Combine("data", "reminders.json"));
    }

    public Task AddAsync(Reminder reminder)
        => _repository.MutateAsync(data => data[reminder.Id] = reminder);

    public async Task<IReadOnlyList<Reminder>> GetForUserAsync(ulong guildId, ulong userId)
        => (await _repository.GetAllAsync())
            .Where(r => r.GuildId == guildId && r.UserId == userId)
            .OrderBy(r => r.DueAt)
            .ToList();

    public async Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset now)
        => (await _repository.GetAllAsync()).Where(r => r.DueAt <= now).OrderBy(r => r.DueAt).ToList();

    public async Task<bool> ExistsAsync(string id) => await _repository.GetAsync(id) is not null;

    public async Task<bool> RemoveAsync(string id)
    {
        var removed = false;
        await _repository.MutateAsync(data => removed = data.Remove(id));
        return removed;
    }
}
