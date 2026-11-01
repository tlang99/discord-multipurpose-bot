namespace ModBot.Services.Moderation;

/// <summary>
/// A small curated list of common English profanity, used as the built-in text filter
/// (toggled per-guild via <c>/automod profanity-filter</c> on <see cref="Models.GuildSettings.ProfanityFilterEnabled"/>).
/// It is deliberately conservative and generic — slurs or community-specific terms should
/// be added per-guild via <c>/automod word-add</c> instead of growing this list, since what
/// counts as a slur or how aggressively to filter varies a lot by community.
/// </summary>
public static class ProfanityWordList
{
    public static readonly IReadOnlyCollection<string> Default = new HashSet<string>
    {
        "fuck", "shit", "bitch", "asshole", "bastard", "dick", "piss",
        "cunt", "whore", "slut", "douchebag", "motherfucker", "fucker",
        "dumbass", "jackass", "twat", "wanker", "prick"
    };
}
