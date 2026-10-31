namespace ModBot.Configuration;

public class SightengineOptions
{
    public const string SectionName = "Sightengine";

    public string ApiUser { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUser) && !string.IsNullOrWhiteSpace(ApiSecret);

    /// <summary>Minimum score (0-1) across sexual_activity/sexual_display/erotica to treat an image as explicit.</summary>
    public double ExplicitThreshold { get; set; } = 0.5;

    /// <summary>Minimum score (0-1) across very_suggestive/suggestive to treat an image as suggestive.</summary>
    public double SuggestiveThreshold { get; set; } = 0.5;

    /// <summary>Minimum score (0-1) for offensive.prob (hate symbols, obscene gestures) to flag an image.</summary>
    public double OffensiveThreshold { get; set; } = 0.5;
}
