namespace ModBot.Models;

/// <summary>
/// A server's standing music queue: the songs waiting to play, whose turn it is, and where its menu is. Saved, so
/// it outlasts the bot leaving voice and restarts.
/// </summary>
public class MusicQueueState
{
    public ulong GuildId { get; set; }

    /// <summary>The pinned menu, once an admin has posted it with /music-menu.</summary>
    public ulong? MenuChannelId { get; set; }

    public ulong? MenuMessageId { get; set; }

    /// <summary>Whose songs are waiting, in the order they first added one: the turn order. People drop out once
    /// they've nothing left waiting (and rejoin at the end when they add more).</summary>
    public List<ulong> Participants { get; set; } = [];

    /// <summary>Whose song played last, so the next turn goes to the person after them.</summary>
    public ulong? LastPickerId { get; set; }

    /// <summary>Waiting songs, each person's in the order they added them. Play order comes from the turns.</summary>
    public List<QueuedSong> Songs { get; set; } = [];

    /// <summary>Finished songs go back in the queue (for their picker) rather than being used up.</summary>
    public bool LoopQueue { get; set; }
}

public class QueuedSong
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lavalink's encoded track, so it plays without looking it up again.</summary>
    public string Track { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Uri { get; set; }

    public TimeSpan Duration { get; set; }

    public bool IsLiveStream { get; set; }

    public ulong PickerId { get; set; }

    public string PickerName { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
