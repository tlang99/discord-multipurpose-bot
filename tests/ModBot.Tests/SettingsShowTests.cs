using Discord;
using ModBot.Common;
using ModBot.Modules.Settings;

namespace ModBot.Tests;

/// <summary>The "Chatbot personality" field of /settings show.</summary>
public class SettingsShowPersonalityTests
{
    [Fact]
    public void ServersOwnPersonality_IsQuotedAndSaysItsTheServers()
    {
        var field = SettingsModule.PersonalityField("A grumpy pirate.", isCustom: true);

        Assert.StartsWith("> A grumpy pirate\\.", field);
        Assert.EndsWith("\n-# This server's own - change it with `/chat personality set`, or `reset` for the default.",
            field);
    }

    [Fact]
    public void DefaultPersonality_SaysItsTheDefault()
    {
        var field = SettingsModule.PersonalityField("Friendly and casual.", isCustom: false);

        Assert.EndsWith("\n-# The default - give this server its own with `/chat personality set`.", field);
    }

    [Fact]
    public void EveryLine_IsQuoted()
    {
        var field = SettingsModule.PersonalityField("Line one\r\nLine two", isCustom: false);

        Assert.StartsWith("> Line one\n> Line two\n-# ", field);
    }

    [Theory]
    [InlineData("# Pirate", "> \\# Pirate")]
    [InlineData("-# tiny", "> \\-# tiny")]
    [InlineData("- a list", "> \\- a list")]
    [InlineData("> a quote", "> \\> a quote")]
    [InlineData("1. first", "> 1\\. first")]
    public void MarkdownAtTheStartOfALine_IsShownAsTyped(string personality, string expectedStart)
    {
        var field = SettingsModule.PersonalityField(personality, isCustom: false);

        Assert.StartsWith(expectedStart, field);
    }

    [Fact]
    public void InlineMarkdown_CantSwallowTheNote()
    {
        // An open bold or code fence would run into the note if it weren't escaped.
        var field = SettingsModule.PersonalityField("**Loud ```and open", isCustom: true);

        Assert.DoesNotContain("> **", field);
        Assert.DoesNotContain(" ```", field);
        Assert.Contains("\n-# This server's own", field);
    }

    [Fact]
    public void LongestPersonality_FitsDiscordsFieldLimit_AndKeepsTheNote()
    {
        // The longest a server can save, and every character escaped (so it doubles in length).
        var personality = new string('*', 1000);

        foreach (var custom in new[] { true, false })
        {
            var field = SettingsModule.PersonalityField(personality, custom);

            Assert.True(field.Length <= EmbedFieldBuilder.MaxFieldValueLength, $"{field.Length} characters");
            Assert.Contains("…\n-# ", field);
        }
    }

    [Fact]
    public void MaskedLinks_AreNotClickable()
    {
        var field = SettingsModule.PersonalityField("Be nice [click here](https://evil.example)", isCustom: true);

        Assert.Contains("\\[click here\\]", field);
        Assert.DoesNotContain("[click here](", field);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Cut_NeverLeavesAStrayBackslash(int max)
    {
        // Every character escaped, so wherever the cut falls it's next to a backslash.
        var cut = TextTools.QuoteAsTyped(new string('*', 50), max);

        Assert.True(cut.Length <= max);
        Assert.EndsWith("…", cut);
        var body = cut[..^1];
        Assert.Equal(0, (body.Length - body.TrimEnd('\\').Length) % 2);
    }

    [Fact]
    public void ChatPersonalityShow_UsesTheSameEscaping()
    {
        // /chat personality show's embed description goes through QuoteAsTyped too.
        Assert.Equal("> \\# Pirate \\[x\\]", TextTools.QuoteAsTyped("# Pirate [x]", EmbedBuilder.MaxDescriptionLength));
    }

    [Fact]
    public void ShortPersonality_IsNotCut()
    {
        Assert.DoesNotContain("…", SettingsModule.PersonalityField("Short and sweet", isCustom: false));
    }
}
