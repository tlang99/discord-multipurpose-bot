using System.Text.RegularExpressions;

namespace ModBot.Services.Search;

/// <summary>
/// Spots a web search asked for in chat ("search for the new patch notes", "look up the Hardpoint map pool", "google
/// best SMG loadout") with plain rules, like the reminder and poll readers. The bot only searches when asked in so
/// many words: questions it could answer itself, or that just sound like they need the web, stay chat. The request has
/// to open the message, so "how do I search for a song?" isn't one.
/// </summary>
public static partial class SearchRequestReader
{
    /// <summary>The longest search accepted, in characters.</summary>
    public const int MaxQueryLength = 300;

    /// <summary>What to search for, or null if <paramref name="message"/> isn't a search request.</summary>
    public static string? Read(string message)
    {
        var match = RequestRegex().Match(message.Replace('’', '\''));
        if (!match.Success)
        {
            return null;
        }

        var query = TrailingFillerRegex().Replace(match.Groups["query"].Value, "").Trim();

        // "Search for a squad" / "find me players" is someone looking for a group, which LFG handles.
        if (query.Length < 2 || LookingForGroupRegex().IsMatch(query))
        {
            return null;
        }

        return query.Length > MaxQueryLength ? query[..MaxQueryLength].TrimEnd() : query;
    }

    [GeneratedRegex(
        @"^\W*(?:(?:hey|ok|okay|please|pls|yo|can\s+you|could\s+you|would\s+you|will\s+you|go\s+ahead\s+and)[\s,]+)*" +
        @"(?:search(?:\s+(?:the\s+web|the\s+internet|online|google))?(?:\s+for)?|look\s+up|google|web\s+search(?:\s+for)?)" +
        @"[\s:,]+(?<query>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RequestRegex();

    [GeneratedRegex(@"(?:[\s,]+(?:please|pls|for\s+me|thanks|thx))+[\s.!?]*$|[\s.!]+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFillerRegex();

    [GeneratedRegex(@"^(?:a|an|some|more|\d+)?\s*(?:squad|team(?:mates?)?|group|lfg|players?|people)\b(?!\s+(?:of|in|on|from|with)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex LookingForGroupRegex();
}
