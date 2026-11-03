using System.Text.RegularExpressions;
using ModBot.Models;

namespace ModBot.Services.Chat;

/// <summary>
/// Reads LFG details from a chat message ("make an lfg for ranked, need 3 more, mic required") with plain keyword
/// rules, using the playlists and ranks the server set up (/settings lfg-game). Used instead of the model: tested
/// against llama3.2 with a forced answer shape, it invented details on almost every request (a gold rank and "mic
/// required" nobody asked for) and miscounted squads. The vocabulary is the server's own list plus a few fixed words
/// (squad sizes, mic, platform), so rules get it right and predictably; anything not mentioned keeps the /lfg default.
/// </summary>
public static partial class LfgRequestReader
{
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
    };

    /// <summary>The squad size and details asked for, with playlists and ranks from the server's game (blank by
    /// default, when only size, mic and platform are read). No note: free text is left to /lfg.</summary>
    public static (int Players, LfgDetails Details) Read(string message, LfgGameProfile game)
    {
        var text = " " + WhitespaceRegex().Replace(message.ToLowerInvariant(), " ") + " ";

        var rank = ReadRank(text, game);
        var choice = game.Combine(ReadPlaylist(text, game), rank);

        // A rank with a playlist it doesn't go with is dropped here, where /lfg would say so: from chat, posting
        // with the playlist the person named beats refusing.
        return (Math.Clamp(ReadPlayers(text), 2, 6),
            LfgDetails.From(game, choice with { Problem = null }, ReadMic(text), ReadPlatform(text)));
    }

    private static int ReadPlayers(string text)
    {
        // "need 3 more", "2 more people", "one more": that many joining, plus the person asking.
        if (MoreRegex().Match(text) is { Success: true } more && ToNumber(more.Groups[1].Value) is { } joining)
        {
            return joining + 1;
        }

        if (HasWord(text, "duo", "duos")) return 2;
        if (HasWord(text, "trio", "trios")) return 3;
        if (HasWord(text, "quad", "quads")) return 4;

        // "4 stack", "squad of 4", "group of 5", "for 4", "4 players" / "4 people": the whole squad.
        if (TotalRegex().Match(text) is { Success: true } total &&
            ToNumber(total.Groups["n"].Value) is { } size)
        {
            return size;
        }

        return 4;
    }

    private static int? ToNumber(string value)
        => int.TryParse(value, out var number) ? number : NumberWords.TryGetValue(value, out number) ? number : null;

    /// <summary>The highest rank named, checked top down so "diamond, not gold" style mentions settle on the higher one
    /// consistently.</summary>
    private static LfgRankOption? ReadRank(string text, LfgGameProfile game)
        => Enumerable.Reverse(game.Ranks).FirstOrDefault(r => r.AllNames().Any(n => HasWord(text, n)));

    /// <summary>Ranked playlists first (a ranked playlist usually rotates modes, so "ranked hardpoint" is the ranked
    /// one), then the rest in the server's list order, which is how a server says which wins ("hardcore domination"
    /// is Hardcore if it's listed above Domination).</summary>
    private static LfgPlaylistOption? ReadPlaylist(string text, LfgGameProfile game)
    {
        bool Named(LfgPlaylistOption p) => p.AllNames().Any(n => HasWord(text, n));
        return game.Playlists.FirstOrDefault(p => p.Ranked && Named(p)) ??
               game.Playlists.FirstOrDefault(p => !p.Ranked && Named(p));
    }

    private static bool? ReadMic(string text)
    {
        // Optional is checked first: "mic not required" contains "mic ... required".
        if (MicOptionalRegex().IsMatch(text)) return false;
        if (MicRequiredRegex().IsMatch(text)) return true;
        return null;
    }

    private static LfgPlatform ReadPlatform(string text)
    {
        if (PcOnlyRegex().IsMatch(text)) return LfgPlatform.PcOnly;
        if (ConsoleOnlyRegex().IsMatch(text)) return LfgPlatform.ConsoleOnly;
        return LfgPlatform.Crossplay;
    }

    private static bool HasWord(string text, params string[] words)
        => words.Any(word => Regex.IsMatch(text, $@"(?<![\w&]){Regex.Escape(word)}(?![\w&])"));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // No "a"/"an": "need a more chill squad" isn't "one more".
    [GeneratedRegex(@"\b(\d|one|two|three|four|five)\s+more\b")]
    private static partial Regex MoreRegex();

    [GeneratedRegex(@"\b(?<n>\d)[\s-]?(?:stack|man|players?|people|ppl)\b|\b(?:squad|group|team|party)\s+of\s+(?<n>\d|two|three|four|five)\b|\bfor\s+(?<n>\d|two|three|four|five)\b")]
    private static partial Regex TotalRegex();

    [GeneratedRegex(@"\bno\s+mics?\b|\bmics?\s+(?:is\s+|are\s+)?(?:optional|not\s+(?:required|needed)|not\s+necessary)\b|\boptional\s+mics?\b|\b(?:don'?t|do\s+not)\s+need\s+(?:a\s+)?mics?\b")]
    private static partial Regex MicOptionalRegex();

    [GeneratedRegex(@"\bmics?\s+(?:is\s+|are\s+)?(?:required|needed|a\s+must|only)\b|\bneed\s+(?:a\s+)?mics?\b|\bmust\s+have\s+(?:a\s+)?mics?\b|\bwith\s+(?:a\s+)?mics?\b|\bmics?\s+on\b")]
    private static partial Regex MicRequiredRegex();

    [GeneratedRegex(@"\bpc\s+only\b|\bonly\s+pc\b|\bpc\s+players\b")]
    private static partial Regex PcOnlyRegex();

    [GeneratedRegex(@"\bconsole\s+only\b|\bonly\s+console\b|\bconsole\s+players\b")]
    private static partial Regex ConsoleOnlyRegex();
}
