namespace ModBot.Models;

/// <summary>A named entry in a server's LFG game (a playlist or a rank), with other names people use for it.</summary>
public abstract class LfgNamedOption
{
    // The setters turn nulls from a hand-edited settings file into empty values, so nothing reading them can crash.
    public string Name { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Other words for it in chat and in /lfg ("hp", "plat"), lowercase.</summary>
    public List<string> Aliases { get; set => field = value?.Where(a => a is not null).ToList() ?? []; } = [];

    /// <summary>The name and every other name, lowercase. The one list everything matches against: /lfg's lookups and
    /// suggestions, chat, and the duplicate check when a server saves its game, so they can't disagree.</summary>
    public IEnumerable<string> AllNames()
        => Aliases.Prepend(Name).Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0);

    /// <summary>Whether <paramref name="value"/> is exactly one of its names (any case, ignoring outer spaces).</summary>
    public bool IsNamed(string value) => AllNames().Contains(value.Trim().ToLowerInvariant());

    /// <summary>Whether one of its names contains <paramref name="typed"/>, for suggestions as someone types.</summary>
    public bool Matches(string typed)
        => typed.Trim().Length == 0 || AllNames().Any(n => n.Contains(typed.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A playlist or game mode a server's LFG posts can name (e.g. "Hardpoint", "Ranked Play").</summary>
public class LfgPlaylistOption : LfgNamedOption
{
    /// <summary>A ranked playlist: the one a minimum rank belongs to.</summary>
    public bool Ranked { get; set; }
}

/// <summary>A skill rank a server's LFG posts can ask for as a minimum (e.g. "Gold").</summary>
public class LfgRankOption : LfgNamedOption;

/// <summary>A playlist and rank resolved against a server's game, or why they don't go together.</summary>
public record LfgChoice(LfgPlaylistOption? Playlist, LfgRankOption? Rank, string? Problem = null);

/// <summary>
/// The game a server's LFG posts are for: its playlists/modes and its ranks, set with /settings lfg-game. Blank by
/// default (<see cref="Empty"/>): LFG then works without game-specific options (squad size, mic, platform, note).
/// No game is built in; the README has a Call of Duty example to paste.
/// </summary>
public class LfgGameProfile
{
    /// <summary>The game's name ("Call of Duty: MW4"), shown on LFG cards and pings; empty = no name shown.</summary>
    public string Name { get; set => field = value?.Trim() ?? string.Empty; } = string.Empty;

    /// <summary>In order: the first one a chat message names wins.</summary>
    public List<LfgPlaylistOption> Playlists { get; set => field = value?.Where(p => p is not null).ToList() ?? []; } = [];

    /// <summary>Lowest first, so the last is the top rank.</summary>
    public List<LfgRankOption> Ranks { get; set => field = value?.Where(r => r is not null).ToList() ?? []; } = [];

    public static LfgGameProfile Empty => new();

    public bool IsEmpty => Name.Length == 0 && Playlists.Count == 0 && Ranks.Count == 0;

    /// <summary>Whether ranks belong to particular playlists. If no playlist is marked ranked, a rank goes with any.</summary>
    public bool HasRankedPlaylists => Playlists.Any(p => p.Ranked);

    public LfgPlaylistOption? FindPlaylist(string value) => Playlists.FirstOrDefault(p => p.IsNamed(value));

    public LfgRankOption? FindRank(string value) => Ranks.FirstOrDefault(r => r.IsNamed(value));

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
}
