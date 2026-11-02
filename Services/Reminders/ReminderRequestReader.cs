using System.Text.RegularExpressions;

namespace ModBot.Services.Reminders;

/// <summary>
/// Spots a reminder request in a chat message ("remind me in 20 minutes to check the oven") and reads its time and
/// text, with plain rules. A request needs both a reminder phrase and a time <see cref="ReminderTimeParser"/>
/// understands, so "can you remind me how to make an lfg?" or "that reminds me..." aren't taken as requests.
/// </summary>
/// <remarks>
/// Not left to the model: tested against llama3.2, adding "set a reminder" as a fourth answer to the request check
/// made it worse at the existing ones (asking how to make an LFG started posting LFGs again), while this phrase-plus-
/// time rule is unambiguous.
/// </remarks>
public static partial class ReminderRequestReader
{
    /// <summary>The request's due time and what to remind about, or null if the message isn't a reminder request.
    /// Text can come back empty ("remind me in 10 minutes"); the caller asks what for.</summary>
    public static (ReminderTime Time, string Text)? Read(string message, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (!RequestPhraseRegex().IsMatch(message) || ReminderTimeParser.Find(message, now, timeZone) is not { } time)
        {
            return null;
        }

        // What's left once the time and the request phrase are taken out: "remind me in 20 minutes to check the
        // oven" -> "check the oven".
        var text = message.Remove(time.Index, time.Length);
        text = RequestPhraseRegex().Replace(text, " ", 1);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        for (var previous = ""; previous != text;)
        {
            previous = text;
            text = LeadingFillerRegex().Replace(text, "").Trim();
        }

        text = TrailingPunctuationRegex().Replace(text, "").Trim();
        return (time, text);
    }

    /// <summary>"remind me", "set a reminder", "ping me", "don't let me forget", with an optional polite lead-in.</summary>
    [GeneratedRegex(@"(?:\b(?:hey|yo|ok|okay)\b[\s,]*)?(?:\b(?:can|could|would|will)\s+you\s+(?:please\s+)?|\bplease\s+)?(?:\bremind\s+me\b|\bset\s+(?:me\s+)?an?\s+reminder\b|\bmake\s+(?:me\s+)?an?\s+reminder\b|\bping\s+me\b|\bdon'?t\s+let\s+me\s+forget\b)", RegexOptions.IgnoreCase)]
    private static partial Regex RequestPhraseRegex();

    /// <summary>Words left at the start that only join the pieces ("to check the oven", "that the raid starts").
    /// "about" is kept: "about the raid" reads fine in the reminder.</summary>
    [GeneratedRegex(@"^(?:to|that|for|of|please|[,:\-–])(?:\s+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFillerRegex();

    [GeneratedRegex(@"[\s?!.,]+$")]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
