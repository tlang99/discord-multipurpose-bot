using ModBot.Models;

namespace ModBot.Services.Storage;

public interface IReminderStore
{
    Task AddAsync(Reminder reminder);

    /// <summary>The user's pending reminders in a server, soonest first.</summary>
    Task<IReadOnlyList<Reminder>> GetForUserAsync(ulong guildId, ulong userId);

    /// <summary>Every reminder due at or before <paramref name="now"/>, soonest first.</summary>
    Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset now);

    Task<bool> ExistsAsync(string id);

    /// <summary>Removes a reminder; false if it was already gone (delivered or cancelled).</summary>
    Task<bool> RemoveAsync(string id);
}
