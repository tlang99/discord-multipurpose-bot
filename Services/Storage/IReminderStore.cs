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

public enum ReminderCancelResult
{
    Cancelled,

    /// <summary>No such reminder, or it isn't theirs.</summary>
    NotFound,

    /// <summary>It's being delivered right now, so cancelling would be a lie.</summary>
    InFlight,
}

public interface IReminderStore
{
    /// <summary>Adds the reminder unless its ID is taken or the user is at <paramref name="maxPerUser"/>, checked and
    /// added in one step so two requests at once can't both slip past either check.</summary>
    Task<ReminderAddResult> TryAddAsync(Reminder reminder, int maxPerUser);

    /// <summary>The user's pending reminders in a server, soonest first.</summary>
    Task<IReadOnlyList<Reminder>> GetForUserAsync(ulong guildId, ulong userId);

    /// <summary>Reminders to deliver now: due, not waiting out a retry back-off, and not being delivered already.
    /// Soonest first.</summary>
    Task<IReadOnlyList<Reminder>> GetDueAsync(DateTimeOffset now);

    /// <summary>Marks a reminder as being delivered until <paramref name="claimUntil"/>. False if it's gone or another
    /// delivery holds an unexpired claim. Checked and set in one step, so only one delivery can win.</summary>
    Task<bool> TryClaimAsync(string id, DateTimeOffset now, DateTimeOffset claimUntil);

    /// <summary>Removes the user's reminder unless it's being delivered right now, checked in one step with the
    /// delivery's claim. Returns the removed reminder on success.</summary>
    Task<(ReminderCancelResult Result, Reminder? Reminder)> TryCancelAsync(string id, ulong guildId, ulong userId,
        DateTimeOffset now);

    /// <summary>Changes a stored reminder; false if it's gone.</summary>
    Task<bool> UpdateAsync(string id, Action<Reminder> update);

    /// <summary>Removes a reminder; false if it was already gone.</summary>
    Task<bool> RemoveAsync(string id);
}
