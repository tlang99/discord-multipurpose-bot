namespace ModBot.Models;

/// <summary>A playlist or game mode a server's LFG posts can name (e.g. "Hardpoint", "Ranked Play").</summary>
public class LfgPlaylistOption
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Other words for it in chat and in /lfg ("hp", "comp"), lowercase.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>A ranked playlist: the one a minimum rank belongs to.</summary>
    public bool Ranked { get; set; }
}

/// <summary>A skill rank a server's LFG posts can ask for as a minimum (e.g. "Gold").</summary>
public class LfgRankOption
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Other words for it ("plat", "top250"), lowercase.</summary>
    public List<string> Aliases { get; set; } = [];
}

/// <summary>A playlist and rank resolved against a server's game, or why they don't go together.</summary>
public record LfgChoice(LfgPlaylistOption? Playlist, LfgRankOption? Rank, string? Problem = null);

/// <summary>
/// The game a server's LFG posts are for: its playlists/modes and its ranks, set with /settings lfg-game. Blank by
/// default (<see cref="Empty"/>): LFG then works without game-specific options (squad size, mic, platform, note).
/// No game is built in; the README has a Call of Duty example to paste.
/// </summary>
public class LfgGameProfile
{
    /// <summary>In order: the first one a chat message names wins.</summary>
    public List<LfgPlaylistOption> Playlists { get; set; } = [];

    /// <summary>Lowest first, so the last is the top rank.</summary>
    public List<LfgRankOption> Ranks { get; set; } = [];

    public static LfgGameProfile Empty => new();

    public bool IsEmpty => Playlists.Count == 0 && Ranks.Count == 0;

    /// <summary>Whether ranks belong to particular playlists. If no playlist is marked ranked, a rank goes with any.</summary>
    public bool HasRankedPlaylists => Playlists.Any(p => p.Ranked);

    public LfgPlaylistOption? FindPlaylist(string value) => Find(Playlists, value, p => p.Name, p => p.Aliases);

    public LfgRankOption? FindRank(string value) => Find(Ranks, value, r => r.Name, r => r.Aliases);

    public bool IsTopRank(LfgRankOption rank) => Ranks.Count > 0 && ReferenceEquals(Ranks[^1], rank);

    /// <summary>
    /// The rule a playlist and minimum rank follow, for /lfg and chat alike: a rank only means something in a ranked
    /// playlist, so naming one without a playlist picks the first ranked playlist, and naming one with an unranked
    /// playlist is a mistake. Games with no ranked playlists (ranks that aren't tied to a mode) take a rank with any.
    /// </summary>
    public LfgChoice Combine(LfgPlaylistOption? playlist, LfgRankOption? rank)
    {
        if (rank is null || !HasRankedPlaylists)
        {
            return new(playlist, rank);
        }

        if (playlist is null)
        {
            return new(Playlists.First(p => p.Ranked), rank);
        }

        return playlist.Ranked
            ? new(playlist, rank)
            : new(playlist, null, $"A minimum rank only applies to {string.Join(" or ",
                Playlists.Where(p => p.Ranked).Select(p => $"**{p.Name}**"))} - pick that, or leave the rank empty.");
    }

    private static T? Find<T>(IEnumerable<T> options, string value, Func<T, string> name,
        Func<T, IEnumerable<string>> aliases) where T : class
    {
        value = value.Trim();
        return options.FirstOrDefault(o => name(o).Equals(value, StringComparison.OrdinalIgnoreCase) ||
                                           aliases(o).Any(a => a.Equals(value, StringComparison.OrdinalIgnoreCase)));
    }
}
