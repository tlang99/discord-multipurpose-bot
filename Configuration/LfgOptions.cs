namespace ModBot.Configuration;

public class LfgOptions
{
    public const string SectionName = "Lfg";

    /// <summary>How long an LFG post stays open if the squad doesn't fill up (5 minutes to 24 hours).</summary>
    public int ExpiryMinutes { get; set; } = 60;

    /// <summary>The only channel /lfg works in, by name (case-insensitive). Empty allows any channel.</summary>
    public string ChannelName { get; set; } = "lfg-mw4";
}
