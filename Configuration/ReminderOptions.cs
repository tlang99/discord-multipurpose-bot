namespace ModBot.Configuration;

public class ReminderOptions
{
    public const string SectionName = "Reminders";

    /// <summary>Time zone for clock times like "at 5pm" or "tomorrow 9am", as an IANA name ("America/Chicago") or a
    /// Windows one ("Central Standard Time"). Empty uses the time zone of the machine the bot runs on.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>Most pending reminders one person can have per server.</summary>
    public int MaxPerUser { get; set; } = 25;

    /// <summary>Furthest ahead a reminder can be set.</summary>
    public int MaxDays { get; set; } = 365;
}
