namespace ModBot.Models;

/// <summary>
/// Per-guild configuration. New feature areas (leveling, welcome messages, etc.)
/// should add their own settings class rather than growing this one, so that
/// moderation settings stay independent of unrelated features.
/// </summary>
public class GuildSettings
{
    public ulong GuildId { get; set; }

    public ulong? ModLogChannelId { get; set; }

    /// <summary>Off until a server turns it on with /automod enable, so a server that adds the bot isn't moderated (or
    /// its images sent to Sightengine) before it asks to be.</summary>
    public bool AutoModEnabled { get; set; }

    public bool BlockInviteLinks { get; set; } = false;

    /// <summary>Built-in curated profanity list, independent of the custom <see cref="BannedWords"/> list.
    /// Off by default — most servers tolerate mild cussing and only want slurs/extreme terms caught,
    /// which belong in <see cref="BannedWords"/> instead since what counts as "extreme" varies by community.</summary>
    public bool ProfanityFilterEnabled { get; set; } = false;

    public List<string> BannedWords { get; set; } = new();

    // -- Image / GIF moderation (requires Sightengine credentials to be configured) --

    public bool ImageModerationEnabled { get; set; } = true;

    public bool BlockExplicitImages { get; set; } = true;

    /// <summary>Suggestive-but-not-explicit content (swimwear, lingerie, etc.) — off by default, many servers allow it.</summary>
    public bool BlockSuggestiveImages { get; set; } = false;

    public bool BlockOffensiveImages { get; set; } = true;

    /// <summary>When false (default), channels flagged NSFW in Discord are skipped entirely by image moderation.</summary>
    public bool ScanNsfwChannels { get; set; } = false;
}
