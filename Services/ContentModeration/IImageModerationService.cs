namespace ModBot.Services.ContentModeration;

/// <summary>
/// Classifies a publicly-reachable image/GIF URL for explicit, suggestive, and offensive
/// content. Implemented against Sightengine today; swap the registration in Program.cs for
/// a different backend later without touching callers.
/// </summary>
public interface IImageModerationService
{
    bool IsConfigured { get; }

    Task<ImageModerationReport> CheckImageAsync(string imageUrl, CancellationToken cancellationToken = default);
}
