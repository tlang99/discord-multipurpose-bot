using System.Text.RegularExpressions;
using ModBot.Models;

namespace ModBot.Services.Chat;

/// <summary>
/// Reads LFG details from a chat message ("make an lfg for ranked, need 3 more, mic required") with plain keyword
/// rules. Used instead of the model: tested against llama3.2 with a forced answer shape, it invented details on almost
/// every request (a gold rank and "mic required" nobody asked for) and miscounted squads. The vocabulary here is small
/// and fixed, so rules get it right and predictably; anything not mentioned keeps the /lfg default.
/// </summary>
public static partial class LfgRequestReader
{
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["a"] = 1, ["an"] = 1,
    };

    /// <summary>The squad size and details asked for. No note: free text is left to /lfg.</summary>
    public static (int Players, LfgDetails Details) Read(string message)
    {
        var text = " " + WhitespaceRegex().Replace(message.ToLowerInvariant(), " ") + " ";

        var rank = ReadRank(text);
        var playlist = ReadPlaylist(text, rank is not null);

        // As in /lfg, a rank only means something in Ranked Play.
        if (playlist != LfgPlaylist.RankedPlay)
        {
            rank = null;
        }

        return (Math.Clamp(ReadPlayers(text), 2, 6), new LfgDetails(playlist, rank, ReadMic(text), ReadPlatform(text)));
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

    private static LfgRank? ReadRank(string text)
    {
        if (text.Contains("top 250") || text.Contains("top250")) return LfgRank.Top250;
        if (HasWord(text, "iridescent", "iri")) return LfgRank.Iridescent;
        if (HasWord(text, "crimson")) return LfgRank.Crimson;
        if (HasWord(text, "diamond", "diamonds")) return LfgRank.Diamond;
        if (HasWord(text, "platinum", "plat")) return LfgRank.Platinum;
        if (HasWord(text, "gold")) return LfgRank.Gold;
        if (HasWord(text, "silver")) return LfgRank.Silver;
        if (HasWord(text, "bronze")) return LfgRank.Bronze;
        return null;
    }

    /// <summary>Ranked first (it rotates modes, so "ranked hardpoint" is Ranked Play), then Hardcore (its own
    /// playlist, so "hardcore domination" is Hardcore), then the individual modes.</summary>
    private static LfgPlaylist ReadPlaylist(string text, bool rankNamed)
    {
        if (rankNamed || HasWord(text, "ranked", "comp", "competitive")) return LfgPlaylist.RankedPlay;
        if (HasWord(text, "hardcore", "hc")) return LfgPlaylist.Hardcore;
        if (text.Contains("search and destroy") || text.Contains("search & destroy") || HasWord(text, "s&d", "snd", "snd's"))
            return LfgPlaylist.SearchAndDestroy;
        if (HasWord(text, "hardpoint", "hardpoints", "hp")) return LfgPlaylist.Hardpoint;
        if (text.Contains("team deathmatch") || HasWord(text, "tdm")) return LfgPlaylist.TeamDeathmatch;
        if (text.Contains("kill confirmed") || HasWord(text, "kc")) return LfgPlaylist.KillConfirmed;
        if (HasWord(text, "domination", "dom")) return LfgPlaylist.Domination;
        return LfgPlaylist.Any;
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

    [GeneratedRegex(@"\b(\d|one|two|three|four|five|a|an)\s+more\b")]
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
