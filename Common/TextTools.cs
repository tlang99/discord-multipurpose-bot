using Discord;

namespace ModBot.Common;

public static class TextTools
{
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
        var cut = Truncate(quoted, max);
        if (cut.Length == quoted.Length)
        {
            return cut;
        }

        // An odd number of backslashes right before the "…" means the cut fell between a backslash and the character
        // it escaped, so the last one would escape the "…" and show as a stray "\".
        var body = cut[..^1];
        var backslashes = body.Length - body.TrimEnd('\\').Length;
        return backslashes % 2 == 1 ? body[..^1] + "…" : cut;
    }

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
}
