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

    /// <summary>Whether a member's own message is checked by auto-mod: auto-mod is on and they aren't exempt. This is
    /// what /automod enable switches.</summary>
    public static bool AppliesTo(GuildSettings settings, IGuildUser? author)
        => settings.AutoModEnabled && !IsExempt(author);

    /// <summary>
    /// Whether text the bot is about to post is checked: always, auto-mod on or off, unless it's an exempt member's
    /// own words (a moderator's poll or reminder). The auto-mod switch only decides whether members' messages are
    /// scanned and removed; it was never meant to let the bot say what the server's word list forbids. Pass null as
    /// <paramref name="author"/> for the bot's own or the model's words, which are always checked. A server with no
    /// banned words and the profanity filter off has nothing to check, so this changes nothing there.
    /// </summary>
    public static bool AppliesToBotPost(IGuildUser? author) => !IsExempt(author);

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
