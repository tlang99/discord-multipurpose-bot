using Discord;
using ModBot.Common;

namespace ModBot.Tests;

/// <summary>Common/TextTools: cutting text, and showing what people typed exactly as typed.</summary>
public class TextToolsTests
{
    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is too long", 10, "this is t…")]
    public void Truncate_cuts_with_an_ellipsis(string value, int max, string expected)
    {
        var cut = TextTools.Truncate(value, max);

        Assert.Equal(expected, cut);
        Assert.True(cut.Length <= max);
    }

    [Fact]
    public void Truncate_never_splits_an_emoji()
    {
        // "abcd" + 😀 (two UTF-16 units) + "efgh": a cut to 6 would otherwise keep only the emoji's first half.
        var cut = TextTools.Truncate("abcd😀efgh", 6);

        Assert.Equal("abcd…", cut);
        Assert.DoesNotContain(cut, char.IsSurrogate);
    }

    [Fact]
    public void Escape_stops_masked_links()
    {
        var escaped = TextTools.Escape("[free nitro](https://evil.example)");

        Assert.StartsWith(@"\[free nitro\]", escaped);
        Assert.DoesNotContain("[free nitro](", escaped);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void QuoteAsTyped_cut_never_leaves_a_stray_backslash(int max)
    {
        // Every character escaped, so wherever the cut falls it's next to a backslash.
        var cut = TextTools.QuoteAsTyped(new string('*', 50), max);

        Assert.True(cut.Length <= max);
        Assert.EndsWith("…", cut);
        var body = cut[..^1];
        Assert.Equal(0, (body.Length - body.TrimEnd('\\').Length) % 2);
    }

    [Fact]
    public void QuoteAsTyped_escapes_headings_and_links()
        // /chat personality show and /settings show both use it.
        => Assert.Equal(@"> \# Pirate \[x\]", TextTools.QuoteAsTyped("# Pirate [x]", EmbedBuilder.MaxDescriptionLength));

    /// <summary>
    /// Markdown that only works at the start of a line. Some of it is escaped by Discord.Net's Format.Sanitize rather
    /// than by TextTools itself ("*", "#", ">", and the "." of "1."), so these pin that: if a Discord.Net update stops
    /// escaping one, this fails instead of a personality quietly turning into a list or heading. "+" isn't Discord
    /// markdown, so it's left alone.
    /// </summary>
    [Theory]
    [InlineData("* a list", @"> \* a list")]
    [InlineData("- a list", @"> \- a list")]
    [InlineData("# heading", @"> \# heading")]
    [InlineData("-# small", @"> \-# small")]
    [InlineData("> a quote", @"> \> a quote")]
    [InlineData("1. first", @"> 1\. first")]
    [InlineData("+ plus", "> + plus")]
    public void QuoteAsTyped_escapes_markdown_at_the_start_of_a_line(string typed, string expected)
        => Assert.Equal(expected, TextTools.QuoteAsTyped(typed, EmbedBuilder.MaxDescriptionLength));
}
