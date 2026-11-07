namespace ModBot.Configuration;

public class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Optional: a second bot account's token, for music. Discord gives a bot one voice connection per server, so
    /// without it, starting music stops the listening for "hey ModBot"; with it, that account plays the music and
    /// both run at once. Make it its own application in the Discord Developer Portal and invite it to the server
    /// (voice/README.md). Set it like the main token: user-secrets or the MUSIC_BOT_TOKEN environment variable.
    /// </summary>
    public string MusicToken { get; set; } = string.Empty;

    /// <summary>
    /// When set, slash commands are registered to this single guild instead of globally.
    /// Guild registration is instant and ideal for development; global registration
    /// can take up to an hour to propagate, so leave this set until you're ready to ship.
    /// </summary>
    public ulong? TestGuildId { get; set; }
}
