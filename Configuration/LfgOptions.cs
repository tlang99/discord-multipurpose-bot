namespace ModBot.Configuration;

public class LfgOptions
{
    public const string SectionName = "Lfg";

    /// <summary>How long an LFG post stays open if the squad doesn't fill up (5 minutes to 24 hours).</summary>
    public int ExpiryMinutes { get; set; } = 60;

    /// <summary>Opt-in role pinged by new LFG posts (created by /lfg-setup). Empty turns pings off.</summary>
    public string PingRoleName { get; set; } = "LFG";

    /// <summary>Minimum time between role pings from the same person's posts, so posting and cancelling repeatedly
    /// can't spam everyone who opted in. The post itself is still made, just without the ping.</summary>
    public int PingCooldownMinutes { get; set; } = 10;
}
