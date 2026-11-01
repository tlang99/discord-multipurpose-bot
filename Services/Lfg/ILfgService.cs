using ModBot.Models;

namespace ModBot.Services.Lfg;

public enum LfgOutcome
{
    /// <summary>The action worked and the post is still open.</summary>
    Updated,

    /// <summary>The action worked and the post is now closed because the squad is full or the host started it.
    /// The squad should be pinged.</summary>
    SquadReady,

    /// <summary>The host cancelled the post (or a moderator removed it).</summary>
    Cancelled,

    /// <summary>The post closed recently (filled, started, cancelled or expired) and its message already shows that,
    /// e.g. someone clicked Join a moment after the last spot went. Don't touch the message.</summary>
    AlreadyClosed,

    /// <summary>The service doesn't know this post at all, e.g. it's from before a restart. Its message may still
    /// show open buttons.</summary>
    NotFound,

    AlreadyJoined,
    NotJoined,
    Full,
    NotHost,
    HostCantLeave,
    NeedsMorePlayers,
}

/// <param name="Post">The post as it is after the action (a snapshot, safe to read without locking), or null if it
/// wasn't found.</param>
public record LfgResult(LfgOutcome Outcome, LfgPost? Post);

/// <summary>
/// Keeps track of open LFG posts and enforces the rules for joining, leaving and starting them. Holds no Discord
/// types, so the rules can be tested without a bot connection. Posts are kept in memory only: after a restart, old
/// posts' buttons report that the post is no longer active.
/// </summary>
public interface ILfgService
{
    /// <summary>Creates a post with the host as its first member. Fails, returning the existing post, if the host
    /// already has an open post in this server.</summary>
    (LfgPost Post, bool Created) Create(ulong guildId, ulong channelId, ulong hostId, LfgMode mode, int size,
        LfgDetails details, string? note, TimeSpan lifetime);

    /// <summary>Whether a new post by this host should ping the LFG role: at most once per <paramref name="cooldown"/>
    /// per host, so posting and cancelling repeatedly can't spam everyone who opted in. Records the ping when it
    /// returns true.</summary>
    bool TryClaimRolePing(ulong guildId, ulong hostId, TimeSpan cooldown);

    /// <summary>Records which message shows the post, once it's been sent.</summary>
    void AttachMessage(Guid postId, ulong messageId);

    /// <summary>Removes a post whose message couldn't be sent.</summary>
    void Discard(Guid postId);

    LfgResult Join(Guid postId, ulong userId);
    LfgResult Leave(Guid postId, ulong userId);

    /// <summary>Closes the post early with whoever has joined. Host only, and needs at least one other member.</summary>
    LfgResult Start(Guid postId, ulong userId);

    /// <summary>Removes the post. The host can always cancel; <paramref name="isModerator"/> lets moderators cancel
    /// anyone's post.</summary>
    LfgResult Cancel(Guid postId, ulong userId, bool isModerator);

    /// <summary>Removes and returns every post whose expiry time has passed.</summary>
    IReadOnlyList<LfgPost> TakeExpired(DateTimeOffset now);
}
