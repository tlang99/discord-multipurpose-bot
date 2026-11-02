using Discord;

namespace ModBot.Modules.Music;

/// <summary>
/// A music session in one server: people build up a queue together, then take turns (see <see cref="FairOrder"/>)
/// once the host starts it, and vote to skip songs. Lives in memory only: the player it drives doesn't survive a
/// restart either.
/// </summary>
/// <remarks>Fields are only changed under <see cref="Sync"/>, never held across an await. Changes that also touch the
/// player's queue take the server's queue lock first, then <see cref="Sync"/>.</remarks>
public sealed class MusicSession
{
    public MusicSession(ulong guildId, ulong hostId, ITextChannel channel, DateTimeOffset now)
    {
        GuildId = guildId;
        HostId = hostId;
        Channel = channel;
        LastActivity = now;
    }

    public object Sync { get; } = new();

    /// <summary>Goes in the menu's button IDs, so buttons on an old session's menu don't act on a new one.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public ulong GuildId { get; }

    /// <summary>Who opened it, or who it passed to when they left. Starts and ends it.</summary>
    public ulong HostId { get; set; }

    /// <summary>Where the menu is, and where songs are announced.</summary>
    public ITextChannel Channel { get; }

    /// <summary>The menu message, once posted.</summary>
    public IUserMessage? Menu { get; set; }

    /// <summary>One menu edit at a time, so an older picture can't land after a newer one.</summary>
    public SemaphoreSlim MenuLock { get; } = new(1, 1);

    /// <summary>False while the queue is being built; true once the host has started playback.</summary>
    public bool Started { get; set; }

    public bool Ended { get; set; }

    public DateTimeOffset LastActivity { get; set; }

    /// <summary>Everyone who has added a song, in the order they first did: the turn order.</summary>
    public List<ulong> Participants { get; } = [];

    /// <summary>Songs added before the session started. Once it starts they move to the player's queue.</summary>
    public List<SessionTrackItem> Lobby { get; } = [];

    /// <summary>Whose song is playing, so the next turn goes to the person after them.</summary>
    public ulong? CurrentPickerId { get; set; }

    /// <summary>The song the votes are for; votes reset when it changes.</summary>
    public Guid? VoteEntryId { get; set; }

    public HashSet<ulong> SkipVotes { get; } = [];
}
