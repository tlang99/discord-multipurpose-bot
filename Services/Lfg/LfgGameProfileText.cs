using ModBot.Models;

namespace ModBot.Services.Lfg;

/// <summary>
/// The text form of a server's LFG game, as typed into /settings lfg-game: one entry per line, <c>Name : alias,
/// alias</c>, with a <c>*</c> after a playlist's name to mark it ranked. Ranks go lowest first. Both boxes empty means
/// blank. Discord-free so the rules can be tested.
/// </summary>
/// <example>
/// <code>
/// Ranked Play* : ranked, comp
/// Hardpoint : hp
/// </code>
/// </example>
public static class LfgGameProfileText
{
    /// <summary>Discord shows at most 25 suggestions for an option, so a longer list couldn't all be offered.</summary>
    public const int MaxEntries = 25;

    public const int MaxNameLength = 50;
    public const int MaxAliasLength = 30;
    public const int MaxAliasesPerEntry = 10;

    /// <summary>The pop-up's text box limit, comfortably above 25 full lines.</summary>
    public const int MaxBoxLength = 4000;

    /// <summary>The profile the two boxes describe, or what's wrong with them (naming the line). Both empty is the
    /// blank profile.</summary>
    public static (LfgGameProfile? Profile, string? Problem) Parse(string? playlists, string? ranks)
    {
        var game = new LfgGameProfile();

        var (playlistEntries, playlistProblem) = ParseLines(playlists, "Playlists", allowRanked: true);
        if (playlistProblem is not null)
        {
            return (null, playlistProblem);
        }

        var (rankEntries, rankProblem) = ParseLines(ranks, "Ranks", allowRanked: false);
        if (rankProblem is not null)
        {
            return (null, rankProblem);
        }

        game.Playlists = playlistEntries.Select(e => new LfgPlaylistOption
        {
            Name = e.Name, Aliases = e.Aliases, Ranked = e.Ranked,
        }).ToList();
        game.Ranks = rankEntries.Select(e => new LfgRankOption { Name = e.Name, Aliases = e.Aliases }).ToList();
        return (game, null);
    }

    /// <summary>The two boxes' text for <paramref name="game"/>, to pre-fill the pop-up with what's set now.</summary>
    public static (string Playlists, string Ranks) Format(LfgGameProfile game)
        => (string.Join("\n", game.Playlists.Select(p => Line(p.Name + (p.Ranked ? "*" : ""), p.Aliases))),
            string.Join("\n", game.Ranks.Select(r => Line(r.Name, r.Aliases))));

    private static string Line(string name, List<string> aliases)
        => aliases.Count == 0 ? name : $"{name} : {string.Join(", ", aliases)}";

    private sealed record Entry(string Name, List<string> Aliases, bool Ranked);

    private static (List<Entry> Entries, string? Problem) ParseLines(string? text, string box, bool allowRanked)
    {
        var entries = new List<Entry>();
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            string Problem(string what) => $"{box}, line {i + 1} (`{Shorten(line)}`): {what}";

            var colon = line.IndexOf(':');
            var name = (colon < 0 ? line : line[..colon]).Trim();
            var ranked = false;
            if (name.EndsWith('*'))
            {
                if (!allowRanked)
                {
                    return ([], Problem("a `*` only marks a ranked **playlist**; ranks don't take one."));
                }

                ranked = true;
                name = name[..^1].TrimEnd();
            }

            if (name.Length is 0 or > MaxNameLength)
            {
                return ([], Problem($"the name must be 1-{MaxNameLength} characters."));
            }

            var aliases = colon < 0
                ? []
                : line[(colon + 1)..].Split(',').Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0)
                    .Distinct().ToList();
            if (aliases.FirstOrDefault(a => a.Length > MaxAliasLength) is { } longAlias)
            {
                return ([], Problem($"`{Shorten(longAlias)}` is longer than {MaxAliasLength} characters."));
            }

            if (aliases.Count > MaxAliasesPerEntry)
            {
                return ([], Problem($"at most {MaxAliasesPerEntry} other names per line."));
            }

            // A name or alias pointing at two entries would make /lfg and chat pick one at random.
            var words = aliases.Prepend(name.ToLowerInvariant());
            if (entries.FirstOrDefault(e => words.Any(w => e.Name.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                                                           e.Aliases.Contains(w))) is { } clash)
            {
                return ([], Problem($"a name here is already used by **{clash.Name}**."));
            }

            entries.Add(new Entry(name, aliases, ranked));
            if (entries.Count > MaxEntries)
            {
                return ([], $"{box}: at most {MaxEntries} lines (Discord can only suggest {MaxEntries}).");
            }
        }

        return (entries, null);
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..39] + "…";
}
