namespace ModBot.Configuration;

public class LfgOptions
{
    public const string SectionName = "Lfg";

    /// <summary>How long an LFG post stays open if the squad doesn't fill up (5 minutes to 24 hours).</summary>
    public int ExpiryMinutes { get; set; } = 60;
}
