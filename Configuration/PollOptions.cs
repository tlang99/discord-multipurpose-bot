namespace ModBot.Configuration;

public class PollOptions
{
    public const string SectionName = "Polls";

    /// <summary>How long a poll runs when nobody says (1 to 768 hours).</summary>
    public int DefaultHours { get; set; } = 24;

    /// <summary>Minimum time between polls from the same person, so nobody can flood a channel with them.</summary>
    public int CooldownSeconds { get; set; } = 60;
}
