using System.Text;
using System.Text.RegularExpressions;
using ModBot.Common;

namespace ModBot.Services.Voice;

/// <summary>
/// A chat reply made fit to read aloud: no links, mentions, emoji or markdown (said aloud, they're noise: "asterisk
/// asterisk", a whole web address), and only the first few sentences, so the bot doesn't talk over people. The full
/// reply is still in the channel's chat.
/// </summary>
public static partial class SpokenText
{
    /// <summary><paramref name="reply"/> ready to speak, at most about <paramref name="maxLength"/> characters in
    /// whole sentences; "" if nothing's left to say.</summary>
    /// <param name="now">For reading Discord timestamps as "in 20 minutes"; the current time when not given.</param>
    public static string From(string? reply, int maxLength, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(reply) || maxLength <= 0)
        {
            return "";
        }

        var at = now ?? DateTimeOffset.UtcNow;
        var text = MarkdownLinkRegex().Replace(reply, "$1");
        text = BareLinkRegex().Replace(text, "");
        text = TimestampRegex().Replace(text, m => ReadTimestamp(m.Groups[1].Value, at));
        text = DiscordTagRegex().Replace(text, ""); // mentions, channels, roles, custom emoji
        text = CodeBlockRegex().Replace(text, "");
        text = LinePrefixRegex().Replace(text, ""); // headings, quotes, list bullets
        text = MarkdownRegex().Replace(text, "");
        text = EscapeRegex().Replace(text, "$1");
        text = WithoutEmoji(text);
        text = SpacesRegex().Replace(text, " ");
        text = SpaceBeforePunctuationRegex().Replace(text, "$1").Trim();
        return FirstSentences(text, maxLength);
    }

    /// <summary>A Discord timestamp's seconds, read aloud; "" for one no date can hold (the text is from the model or a
    /// web page, so it can say anything).</summary>
    private static string ReadTimestamp(string seconds, DateTimeOffset now)
        => long.TryParse(seconds, out var unix) &&
           unix >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && unix <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? Relative(DateTimeOffset.FromUnixTimeSeconds(unix), now)
            : "";

    /// <summary>"in 20 minutes", "2 hours ago": how a time reads aloud, whatever format Discord shows it in (the
    /// listener's time zone isn't known, so never a clock time).</summary>
    public static string Relative(DateTimeOffset time, DateTimeOffset now)
    {
        var gap = time - now;
        var span = gap.Duration();
        var amount = span.TotalMinutes < 1 ? null
            : span.TotalHours < 1 ? TextTools.Count((int)Math.Round(span.TotalMinutes), "minute")
            : span.TotalDays < 1 ? TextTools.Count((int)Math.Round(span.TotalHours), "hour")
            : TextTools.Count((int)Math.Round(span.TotalDays), "day");
        return amount is null ? "now" : gap > TimeSpan.Zero ? $"in {amount}" : $"{amount} ago";
    }

    /// <summary>As many whole sentences as fit in <paramref name="maxLength"/>; a first sentence too long on its own is
    /// cut at a word.</summary>
    private static string FirstSentences(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var kept = 0;
        foreach (Match end in SentenceEndRegex().Matches(text))
        {
            if (end.Index + end.Length > maxLength)
            {
                break;
            }

            kept = end.Index + end.Length;
        }

        if (kept > 0)
        {
            return text[..kept].Trim();
        }

        var space = text.LastIndexOf(' ', maxLength);
        return text[..(space > 0 ? space : maxLength)].TrimEnd(',', ';', ':', ' ') + ".";
    }

    /// <summary>Emoji and other pictographs, plus the joiners and variation marks that build them.</summary>
    private static string WithoutEmoji(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.OtherSymbol ||
                rune.Value is 0x200D or (>= 0xFE00 and <= 0xFE0F) or (>= 0x1F3FB and <= 0x1F3FF))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"<?\b(?:https?://|www\.)\S+>?", RegexOptions.IgnoreCase)]
    private static partial Regex BareLinkRegex();

    [GeneratedRegex(@"<t:(-?\d{1,12})(?::\w)?>")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"<(?:@[!&]?\d+|#\d+|a?:\w+:\d+)>")]
    private static partial Regex DiscordTagRegex();

    [GeneratedRegex(@" +([.,!?;:])")]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"^\s*(?:#{1,6}\s+|>+\s*|[-*•]\s+|\d+[.)]\s+)", RegexOptions.Multiline)]
    private static partial Regex LinePrefixRegex();

    /// <summary>Bold, italics, underline, strike-through, spoilers and code marks (not an escaped one).</summary>
    [GeneratedRegex(@"(?<!\\)(?:\*+|__+|~~|\|\||`+)|(?<![\\\w])_(?=\S)|(?<=\S)(?<!\\)_(?!\w)")]
    private static partial Regex MarkdownRegex();

    [GeneratedRegex(@"\\([\\*_~`|>#\[\]()-])")]
    private static partial Regex EscapeRegex();

    [GeneratedRegex(@"[.!?…]+[""')\]]*(?=\s|$)")]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
