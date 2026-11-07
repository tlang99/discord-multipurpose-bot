using Discord;

namespace ModBot.Common;

public static class TextTools
{
    /// <summary>The longest a time in a song can be (<see cref="ParseTime"/>): longer than any song.</summary>
    private static readonly TimeSpan LongestSongTime = TimeSpan.FromHours(12);

    /// <summary>The most digits in one part of a time ("43200" seconds is 12 hours), so a huge number isn't parsed.
    /// </summary>
    private const int MostTimeDigits = 5;

    /// <summary>A point in a song as people write it: "1:30", "1:02:03", or plain seconds ("90"). Null if it isn't
    /// one (or is past <see cref="LongestSongTime"/>).</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length == 0 || p.Length > MostTimeDigits || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        // Every part after the first is under 60 ("1:75" isn't a time).
        var numbers = parts.Select(int.Parse).ToArray();
        if (numbers.Skip(1).Any(n => n >= 60))
        {
            return null;
        }

        var time = TimeSpan.FromSeconds(numbers.Aggregate(0L, (total, n) => total * 60 + n));
        return time <= LongestSongTime ? time : null;
    }

    /// <summary>Letters and digits only, lower case: "Mod-Bot," is "modbot", "Mr. Brightside" is "mrbrightside".
    /// For matching what's said or typed loosely.</summary>
    public static string Squash(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary><paramref name="value"/> cut to at most <paramref name="max"/> characters, ending in "…" when
    /// cut.</summary>
    public static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        // Never cut between the two halves of an emoji (or any character outside the basic range), which would
        // leave half a character that shows up as garbage.
        var keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(value[keep - 1]))
        {
            keep--;
        }

        return value[..keep].TrimEnd() + "…";
    }

    /// <summary>Text from users or uploaders with its inline markdown shown as typed. Format.Sanitize doesn't escape
    /// square brackets, and embeds render [text](url) as a link: without escaping them, "[click](https://evil)" would
    /// turn into a clickable link, or a song title could break out of the [title](url) link it sits in.</summary>
    public static string Escape(string value) => Format.Sanitize(value).Replace("[", "\\[").Replace("]", "\\]");

    /// <summary>Several lines someone wrote (a chatbot personality), shown exactly as typed, as a quote, cut to at most
    /// <paramref name="max"/> characters. Markdown that only works at the start of a line (# headings, -# small text,
    /// - lists, > quotes) is escaped too, and the cut never leaves a markdown span open or a stray backslash.</summary>
    public static string QuoteAsTyped(string value, int max)
    {
        var quoted = string.Join("\n", value.Trim().Split('\n').Select(line => "> " + EscapeLineStart(Escape(line.TrimEnd('\r')))));
        return TruncateEscaped(quoted, max);
    }

    /// <summary><see cref="Truncate"/> for text that's already escaped: the cut never falls between a backslash and
    /// the character it escapes. (Otherwise the backslash would escape the "…" and show as a stray "\".)</summary>
    public static string TruncateEscaped(string escaped, int max)
    {
        var cut = Truncate(escaped, max);
        if (cut.Length == escaped.Length)
        {
            return cut;
        }

        // An odd number of backslashes right before the "…" means the last one was escaping a character that got cut.
        var body = cut[..^1];
        var backslashes = body.Length - body.TrimEnd('\\').Length;
        return backslashes % 2 == 1 ? body[..^1] + "…" : cut;
    }

    /// <summary>What someone typed, shown in a code span exactly as typed (`like this`). Inside a code span escaping
    /// doesn't work - a backslash shows as a backslash - so a backtick in the text is handled by the delimiters
    /// instead: Discord's double-backtick span (`` a`b ``) can hold single backticks. Text with a double backtick
    /// can't be shown that way, so its backticks become ˋ, which looks the same.</summary>
    public static string Code(string value)
    {
        var text = value.Trim();
        if (text.Contains("``"))
        {
            text = text.Replace('`', 'ˋ');
        }

        return text.Contains('`') ? $"`` {text} ``" : $"`{text}`";
    }

    /// <summary>Escapes markdown that only works at the start of a line, on a line <see cref="Escape"/> already ran
    /// on. Format.Sanitize normally escapes "#" and ">" itself, so in practice only "-" (lists, "-#" small text) is
    /// changed here; all three are checked so a Discord.Net update that stops escaping one doesn't open it up.
    /// TextToolsTests pins the result either way.</summary>
    private static string EscapeLineStart(string escapedLine)
    {
        var start = escapedLine.TrimStart();
        return start.Length > 0 && (start[0] is '#' or '-' or '>')
            ? escapedLine[..(escapedLine.Length - start.Length)] + "\\" + start
            : escapedLine;
    }

    /// <summary>"1 song", "3 songs".</summary>
    public static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}

/// <summary>The bot's embed colours.</summary>
public static class Colors
{
    /// <summary>Discord's blurple: help, settings and reminders.</summary>
    public static readonly Color Bot = new(0x5865F2);

    /// <summary>Green: everything music.</summary>
    public static readonly Color Music = new(0x1DB954);

    /// <summary>Darker green: news posts, and web search answers (which are posted like them).</summary>
    public static readonly Color News = new(0x2B, 0x8A, 0x3E);
}

/// <summary>Checks on addresses from outside (feeds, search results, what people type) before they're used.</summary>
public static class WebLinks
{
    /// <summary>True, with the parsed address, if <paramref name="value"/> is an absolute http or https link (only https
    /// with <paramref name="httpsOnly"/>, e.g. for images Discord loads straight from the address). Anything else
    /// (javascript:, file:, a relative path) is refused.</summary>
    public static bool TryParse(string? value, out Uri uri, bool httpsOnly = false)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || !httpsOnly && parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>The site an address is on, without "www." ("www.example.com/a" is "example.com").</summary>
    public static string SiteOf(Uri uri)
        => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
}
