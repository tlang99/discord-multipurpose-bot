using Discord;

namespace ModBot.Common;

public static class TextTools
{
    /// <summary><paramref name="value"/> cut to at most <paramref name="max"/> characters, ending in "…" when
    /// cut.</summary>
    public static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

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
