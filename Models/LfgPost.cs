namespace ModBot.Models;

public enum LfgMode
{
    Multiplayer,
}

public enum LfgPlatform
{
    Crossplay,
    PcOnly,
    ConsoleOnly,
}

/// <summary>Optional details the host picks on /lfg, shown as tags on the card.</summary>
/// <param name="Playlist">A playlist/mode name from the server's <see cref="LfgGameProfile"/>; null = any.</param>
/// <param name="MinRank">A rank name from the server's game; null = any. See <see cref="LfgGameProfile.Combine"/>.</param>
/// <param name="MinRankIsTop">The rank is the game's top one, shown as "Top 250" rather than "Top 250+".</param>
/// <param name="MicRequired">null = not specified.</param>
/// <param name="Game">The server's game name when the post was made (<see cref="LfgGameProfile.Name"/>); null = none.</param>
public record LfgDetails(
    string? Playlist = null,
    string? MinRank = null,
    bool? MicRequired = null,
    LfgPlatform Platform = LfgPlatform.Crossplay,
    bool MinRankIsTop = false,
    string? Game = null)
{
    /// <summary>The details for a playlist and rank resolved with <see cref="LfgGameProfile.Combine"/>. The game name
    /// is copied in, so renaming the game later doesn't change posts already up.</summary>
    public static LfgDetails From(LfgGameProfile game, LfgChoice choice, bool? mic, LfgPlatform platform)
        => new(choice.Playlist?.Name, choice.Rank?.Name, mic, platform,
            choice.Rank is { } rank && game.IsTopRank(rank), game.Name.Length > 0 ? game.Name : null);
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
