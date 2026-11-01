using ModBot.Models;

namespace ModBot.Services.Storage;

public enum ReminderAddResult
{
    Added,

    /// <summary>Another reminder already has this ID; pick a new one and try again.</summary>
    IdTaken,

    /// <summary>The user already has the maximum number of reminders in that server.</summary>
    LimitReached,
}

public interface IReminderStore
{
    /// <summary>Adds the reminder unless its ID is taken or the user is at <paramref name="maxPerUser"/>, checked and
    /// added in one step so two requests at once can't both slip past either check.</summary>
    Task<ReminderAddResult> TryAddAsync(Reminder reminder, int maxPerUser);

    /// <summary>The user's pending reminders in a server, soonest first.</summary>
    Task<IReadOnlyList<Reminder>> GetForUserAsync(ulong guildId, ulong userId);

    /// <summary>Reminders to deliver now: due, and not waiting out a retry back-off. Soonest first.</summary>
    Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset now);

    Task<bool> ExistsAsync(string id);

    /// <summary>Changes a stored reminder; false if it's gone.</summary>
    Task<bool> UpdateAsync(string id, Action<Reminder> update);

    /// <summary>Removes a reminder; false if it was already gone (delivered or cancelled).</summary>
    Task<bool> RemoveAsync(string id);
}
