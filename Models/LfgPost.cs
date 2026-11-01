namespace ModBot.Models;

public enum LfgMode
{
    Multiplayer,
}

/// <summary>
/// An open "looking for group" post. Only open posts exist: once a post fills up, is started, cancelled or expires it
/// is removed, and its buttons then just report that it's no longer active.
/// </summary>
public class LfgPost
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ulong GuildId { get; init; }
    public ulong ChannelId { get; init; }

    /// <summary>The bot's message showing this post. 0 until the message has been sent.</summary>
    public ulong MessageId { get; set; }

    public ulong HostId { get; init; }
    public LfgMode Mode { get; init; } = LfgMode.Multiplayer;

    /// <summary>Total squad size, including the host.</summary>
    public int Size { get; init; }

    public string? Note { get; init; }

    /// <summary>Everyone in the squad so far, host first, in the order they joined.</summary>
    public List<ulong> Members { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }

    public int OpenSpots => Size - Members.Count;
}
