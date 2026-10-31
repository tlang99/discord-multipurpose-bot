namespace ModBot.Configuration;

public class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// When set, slash commands are registered to this single guild instead of globally.
    /// Guild registration is instant and ideal for development; global registration
    /// can take up to an hour to propagate, so leave this set until you're ready to ship.
    /// </summary>
    public ulong? TestGuildId { get; set; }
}
