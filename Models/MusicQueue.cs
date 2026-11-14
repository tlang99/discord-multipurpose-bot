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

    /// <summary>How loud the music plays, as a percentage (100 = as the song is). Kept between sessions.</summary>
    public int VolumePercent { get; set; } = 100;

    /// <summary>The songs played most recently, newest first (the one playing now, if any, is first), for "play that
    /// again", going back, and the menu's History list.</summary>
    public List<QueuedSong> History { get; set; } = [];

    /// <summary>What each person has told DJ mode they like, and the songs they've liked: what it goes by while
    /// they're listening (see <see cref="Modules.Music.DjRules"/>).</summary>
    public List<ListenerTaste> Tastes { get; set; } = [];

    /// <summary>Titles of DJ mode's picks that got skipped, newest first, so it steers away from songs like them.</summary>
    public List<string> DjSkipped { get; set; } = [];
}

/// <summary>One person's taste in music, for DJ mode, in this server.</summary>
public class ListenerTaste
{
    public ulong UserId { get; set; }

    /// <summary>In their own words ("90s hip hop, no country"); blank if they haven't said.</summary>
    public string Taste { get; set; } = string.Empty;

    /// <summary>Songs they've liked, newest first.</summary>
    public List<QueuedSong> Liked { get; set; } = [];
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
