using System.Collections.Concurrent;

namespace ModBot.Common;

/// <summary>
/// A per-person cooldown in each server (chat questions, polls, web searches): claiming a turn is one atomic step, so
/// two requests at the same moment can't both get through. Old entries are dropped as it grows, so it doesn't keep one
/// for everyone who ever asked.
/// </summary>
public sealed class UserCooldowns
{
    /// <summary>Above this many people, expired entries are cleared out on the next claim.</summary>
    private const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _last = new();
    private readonly TimeSpan _cooldown;

    public UserCooldowns(int seconds) => _cooldown = TimeSpan.FromSeconds(Math.Max(0, seconds));

    /// <summary>Takes the person's turn if their cooldown is over; otherwise false, with how long is left.</summary>
    public bool TryClaim(ulong guildId, ulong userId, out TimeSpan wait)
    {
        var now = DateTimeOffset.UtcNow;
        if (_last.Count > PruneAbove)
        {
            Prune(now);
        }

        var allowed = false;
        var last = _last.AddOrUpdate((guildId, userId),
            _ =>
            {
                allowed = true;
                return now;
            },
            (_, previous) =>
            {
                allowed = now - previous >= _cooldown;
                return allowed ? now : previous;
            });
        wait = allowed ? TimeSpan.Zero : last + _cooldown - now;
        return allowed;
    }

    /// <summary>Gives the turn back, e.g. when what it was for failed before doing anything.</summary>
    public void Release(ulong guildId, ulong userId) => _last.TryRemove((guildId, userId), out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, last) in _last)
        {
            if (now - last >= _cooldown)
            {
                _last.TryRemove(new KeyValuePair<(ulong, ulong), DateTimeOffset>(key, last));
            }
        }
    }
}
