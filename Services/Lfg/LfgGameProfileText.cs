using ModBot.Models;
using ModBot.Services.Chat;

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

    /// <summary>Discord's limit for a pop-up text box. 25 lines with every name at full length wouldn't fit, so the
    /// saved text is checked against it too (see <see cref="Parse"/>).</summary>
    public const int MaxBoxLength = 4000;

    /// <summary>Longest game name ("Call of Duty: Modern Warfare 4" is 30). It goes in LFG card titles.</summary>
    public const int MaxGameNameLength = 50;

    /// <summary>The profile the boxes describe, or what's wrong with them (naming the line). All empty is the blank
    /// profile. The game name is only shown, never matched in chat, so it doesn't follow the playlist/rank name rules.
    /// </summary>
    public static (LfgGameProfile? Profile, string? Problem) Parse(string? playlists, string? ranks,
        string? gameName = null)
    {
        // One line: it's a title. Line breaks pasted into the box become spaces.
        var name = string.Join(" ", (gameName ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())).Trim();
        if (name.Length > MaxGameNameLength)
        {
            return (null, $"Game name: at most {MaxGameNameLength} characters.");
        }

        // One set of taken names across both boxes: chat reads both lists from the same message, so a playlist and a
        // rank both called "Diamond" would make "diamond" set both.
        var taken = new Dictionary<string, string>();

        var (playlistEntries, playlistProblem) = ParseLines(playlists, "Playlists", allowRanked: true, taken);
        if (playlistProblem is not null)
        {
            return (null, playlistProblem);
        }

        var (rankEntries, rankProblem) = ParseLines(ranks, "Ranks", allowRanked: false, taken);
        if (rankProblem is not null)
        {
            return (null, rankProblem);
        }

        var game = new LfgGameProfile
        {
            Name = name,
            Playlists = playlistEntries.Select(e => new LfgPlaylistOption
            {
                Name = e.Name, Aliases = e.Aliases, Ranked = e.Ranked,
            }).ToList(),
            Ranks = rankEntries.Select(e => new LfgRankOption { Name = e.Name, Aliases = e.Aliases }).ToList(),
        };

        // The pop-up is pre-filled with the saved lists written out tidily (Format), which can be longer than what was
        // typed ("hp,snd" comes back as "hp, snd"). Saving more than a box holds would leave the server unable to open
        // /settings lfg-game again, so it's refused now, while the admin can trim it.
        var (playlistText, rankText) = Format(game);
        if (playlistText.Length > MaxBoxLength || rankText.Length > MaxBoxLength)
        {
            return (null, $"{(playlistText.Length > MaxBoxLength ? "Playlists" : "Ranks")}: too long to fit back in " +
                          $"this pop-up ({MaxBoxLength} characters once tidied). Use fewer or shorter other names.");
        }

        return (game, null);
    }

    /// <summary>The two boxes' text for <paramref name="game"/>, to pre-fill the pop-up with what's set now.</summary>
    public static (string Playlists, string Ranks) Format(LfgGameProfile game)
        => (string.Join("\n", game.Playlists.Select(p => Line(p.Name + (p.Ranked ? "*" : ""), p.Aliases))),
            string.Join("\n", game.Ranks.Select(r => Line(r.Name, r.Aliases))));

    private static string Line(string name, List<string> aliases)
        => aliases.Count == 0 ? name : $"{name} : {string.Join(", ", aliases)}";

    private sealed record Entry(string Name, List<string> Aliases, bool Ranked);

    /// <param name="taken">Every name used so far, in either box, and whose it is ("the Diamond rank").</param>
    private static (List<Entry> Entries, string? Problem) ParseLines(string? text, string box, bool allowRanked,
        Dictionary<string, string> taken)
    {
        var kind = allowRanked ? "playlist" : "rank";
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

            // Names chat would match in ordinary messages ("need 3 more", "make me an lfg").
            foreach (var word in aliases.Prepend(name))
            {
                if (LfgRequestReader.WhyCantBeAName(word) is { } reason)
                {
                    return ([], Problem($"`{Shorten(word)}` {reason}."));
                }
            }

            if (aliases.Count > MaxAliasesPerEntry)
            {
                return ([], Problem($"at most {MaxAliasesPerEntry} other names per line."));
            }

            // A name pointing at two entries would make /lfg and chat pick one at random. Checked with the same name
            // list they match against (LfgNamedOption.AllNames), so this can't drift from what they resolve.
            var option = new LfgRankOption { Name = name, Aliases = aliases };
            if (option.AllNames().FirstOrDefault(taken.ContainsKey) is { } clash)
            {
                return ([], Problem($"`{clash}` is already used by **{taken[clash]}**."));
            }

            foreach (var word in option.AllNames())
            {
                taken[word] = $"the {name} {kind}";
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
