using ModBot.Models;

namespace ModBot.Services.Lfg;

public class LfgService : ILfgService
{
    // One lock for everything: posts are few and every operation is a handful of list operations, so finer locking
    // wouldn't buy anything. Two people clicking Join for the last spot at once are serialized here.
    private static readonly TimeSpan RememberClosedFor = TimeSpan.FromDays(1);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, LfgPost> _posts = new();

    // Recently closed posts, so late clicks can be told "already closed" without the caller re-rendering the message
    // (which would overwrite the "squad full" card with the stale one the late clicker saw). Pruned after a day.
    private readonly Dictionary<Guid, DateTimeOffset> _closedAt = new();

    // When each host's post last pinged the LFG role, for the ping cooldown. Pruned with _closedAt.
    private readonly Dictionary<(ulong GuildId, ulong HostId), DateTimeOffset> _lastRolePing = new();

    public bool TryClaimRolePing(ulong guildId, ulong hostId, TimeSpan cooldown)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastRolePing.TryGetValue((guildId, hostId), out var last) && now - last < cooldown)
            {
                return false;
            }

            _lastRolePing[(guildId, hostId)] = now;
            return true;
        }
    }

    public (LfgPost Post, bool Created) Create(ulong guildId, ulong channelId, ulong hostId, LfgMode mode, int size,
        LfgDetails details, string? note, TimeSpan lifetime)
    {
        lock (_lock)
        {
            var existing = _posts.Values.FirstOrDefault(p => p.GuildId == guildId && p.HostId == hostId);
            if (existing is not null)
            {
                return (Snapshot(existing), false);
            }

            var now = DateTimeOffset.UtcNow;
            var post = new LfgPost
            {
                GuildId = guildId,
                ChannelId = channelId,
                HostId = hostId,
                Mode = mode,
                Details = details,
                Size = size,
                Note = note,
                Members = [hostId],
                CreatedAt = now,
                ExpiresAt = now + lifetime,
            };
            _posts[post.Id] = post;
            return (Snapshot(post), true);
        }
    }

    public void AttachMessage(Guid postId, ulong messageId)
    {
        lock (_lock)
        {
            if (_posts.TryGetValue(postId, out var post))
            {
                post.MessageId = messageId;
            }
        }
    }

    public void Discard(Guid postId)
    {
        lock (_lock)
        {
            _posts.Remove(postId);
        }
    }

    public LfgResult Join(Guid postId, ulong userId)
    {
        lock (_lock)
        {
            if (!_posts.TryGetValue(postId, out var post))
            {
                return Missing(postId);
            }

            if (post.Members.Contains(userId))
            {
                return new(LfgOutcome.AlreadyJoined, Snapshot(post));
            }

            if (post.OpenSpots <= 0)
            {
                return new(LfgOutcome.Full, Snapshot(post));
            }

            post.Members.Add(userId);
            if (post.OpenSpots > 0)
            {
                return new(LfgOutcome.Updated, Snapshot(post));
            }

            Close(postId);
            return new(LfgOutcome.SquadReady, Snapshot(post));
        }
    }

    public LfgResult Leave(Guid postId, ulong userId)
    {
        lock (_lock)
        {
            if (!_posts.TryGetValue(postId, out var post))
            {
                return Missing(postId);
            }

            if (userId == post.HostId)
            {
                return new(LfgOutcome.HostCantLeave, Snapshot(post));
            }

            return post.Members.Remove(userId)
                ? new(LfgOutcome.Updated, Snapshot(post))
                : new(LfgOutcome.NotJoined, Snapshot(post));
        }
    }

    public LfgResult Start(Guid postId, ulong userId)
    {
        lock (_lock)
        {
            if (!_posts.TryGetValue(postId, out var post))
            {
                return Missing(postId);
            }

            if (userId != post.HostId)
            {
                return new(LfgOutcome.NotHost, Snapshot(post));
            }

            if (post.Members.Count < 2)
            {
                return new(LfgOutcome.NeedsMorePlayers, Snapshot(post));
            }

            Close(postId);
            return new(LfgOutcome.SquadReady, Snapshot(post));
        }
    }

    public LfgResult Cancel(Guid postId, ulong userId, bool isModerator)
    {
        lock (_lock)
        {
            if (!_posts.TryGetValue(postId, out var post))
            {
                return Missing(postId);
            }

            if (userId != post.HostId && !isModerator)
            {
                return new(LfgOutcome.NotHost, Snapshot(post));
            }

            Close(postId);
            return new(LfgOutcome.Cancelled, Snapshot(post));
        }
    }

    public IReadOnlyList<LfgPost> TakeExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            var expired = _posts.Values.Where(p => p.ExpiresAt <= now).ToList();
            foreach (var post in expired)
            {
                Close(post.Id);
            }

            // Runs every 30 seconds, so it doubles as the place to forget long-closed posts.
            foreach (var id in _closedAt.Where(c => now - c.Value > RememberClosedFor).Select(c => c.Key).ToList())
            {
                _closedAt.Remove(id);
            }

            foreach (var key in _lastRolePing.Where(p => now - p.Value > RememberClosedFor).Select(p => p.Key).ToList())
            {
                _lastRolePing.Remove(key);
            }

            return expired.Select(Snapshot).ToList();
        }
    }

    /// <summary>Call with the lock held.</summary>
    private void Close(Guid postId)
    {
        _posts.Remove(postId);
        _closedAt[postId] = DateTimeOffset.UtcNow;
    }

    /// <summary>Call with the lock held.</summary>
    private LfgResult Missing(Guid postId)
        => new(_closedAt.ContainsKey(postId) ? LfgOutcome.AlreadyClosed : LfgOutcome.NotFound, null);

    /// <summary>A copy for callers to read outside the lock, so they never see the member list mid-change.</summary>
    private static LfgPost Snapshot(LfgPost post) => new()
    {
        Id = post.Id,
        GuildId = post.GuildId,
        ChannelId = post.ChannelId,
        MessageId = post.MessageId,
        HostId = post.HostId,
        Mode = post.Mode,
        Details = post.Details,
        Size = post.Size,
        Note = post.Note,
        Members = [.. post.Members],
        CreatedAt = post.CreatedAt,
        ExpiresAt = post.ExpiresAt,
    };
}
