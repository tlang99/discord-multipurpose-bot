namespace ModBot.Services.ContentModeration;

/// <summary>
/// Raw scores (0-1) from the image moderation backend. Deciding what to do about them
/// (which thresholds, which categories a guild cares about) is left to the caller, so the
/// backend client stays a thin data source and the moderation policy can change independently.
/// </summary>
public record ImageModerationReport(
    bool Success,
    double ExplicitScore,
    double SuggestiveScore,
    double OffensiveScore,
    string? Error = null)
{
    public static ImageModerationReport Failed(string error) => new(false, 0, 0, 0, error);
}
