namespace ModBot.Models;

public enum LfgMode
{
    Multiplayer,
}

public enum LfgPlaylist
{
    Any,
    RankedPlay,
    Hardpoint,
    SearchAndDestroy,
    TeamDeathmatch,
    Domination,
    KillConfirmed,
    Hardcore,
}

/// <summary>Ranked Play skill divisions, lowest first, so "at least" comparisons work.</summary>
public enum LfgRank
{
    Bronze,
    Silver,
    Gold,
    Platinum,
    Diamond,
    Crimson,
    Iridescent,
    Top250,
}

public enum LfgPlatform
{
    Crossplay,
    PcOnly,
    ConsoleOnly,
}

/// <summary>Optional details the host picks on /lfg, shown as tags on the card.</summary>
/// <param name="MinRank">Only meaningful for <see cref="LfgPlaylist.RankedPlay"/>.</param>
/// <param name="MicRequired">null = not specified.</param>
public record LfgDetails(
    LfgPlaylist Playlist = LfgPlaylist.Any,
    LfgRank? MinRank = null,
    bool? MicRequired = null,
    LfgPlatform Platform = LfgPlatform.Crossplay);

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
    public LfgDetails Details { get; init; } = new();

    /// <summary>Total squad size, including the host.</summary>
    public int Size { get; init; }

    public string? Note { get; init; }

    /// <summary>Everyone in the squad so far, host first, in the order they joined.</summary>
    public List<ulong> Members { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }

    public int OpenSpots => Size - Members.Count;
}
