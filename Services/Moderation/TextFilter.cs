using System.Text.RegularExpressions;
using Discord;
using ModBot.Models;

namespace ModBot.Services.Moderation;

/// <summary>
/// The server's text rules (invite links, custom banned words, the built-in profanity list), shared by auto-mod and
/// anything else that needs to check text against them, such as the chatbot's replies.
/// </summary>
public static class TextFilter
{
    private static readonly Regex InviteLinkRegex = new(
        @"(discord\.gg|discord(?:app)?\.com/invite)/\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Members with Manage Messages aren't held to the word rules, anywhere they apply (auto-mod, chat,
    /// reminders, song titles).</summary>
    public static bool IsExempt(IGuildUser? user) => user?.GuildPermissions.ManageMessages == true;

    /// <summary>Whether text from <paramref name="author"/> is checked: auto-mod is on and they aren't exempt. Pass
    /// null for the bot's own text, which is always checked while auto-mod is on.</summary>
    public static bool AppliesTo(GuildSettings settings, IGuildUser? author)
        => settings.AutoModEnabled && !IsExempt(author);

    /// <summary>Why <paramref name="content"/> breaks the guild's text rules (e.g. "contained profanity"), or null if
    /// it doesn't. Ignores <see cref="GuildSettings.AutoModEnabled"/>; callers decide whether that applies.</summary>
    public static string? FindViolation(string content, GuildSettings settings)
    {
        if (settings.BlockInviteLinks && InviteLinkRegex.IsMatch(content))
        {
            return "contained a server invite link";
        }

        var normalizedContent = TextNormalizer.Normalize(content);

        if (settings.BannedWords.Any(word => ContainsNormalized(normalizedContent, word)))
        {
            return "contained a banned word or phrase";
        }

        if (settings.ProfanityFilterEnabled && ProfanityWordList.Default.Any(word => ContainsNormalized(normalizedContent, word)))
        {
            return "contained profanity";
        }

        return null;
    }

    private static bool ContainsNormalized(string normalizedContent, string word)
    {
        var normalizedWord = TextNormalizer.Normalize(word);
        return normalizedWord.Length > 0 && normalizedContent.Contains(normalizedWord, StringComparison.Ordinal);
    }
}
