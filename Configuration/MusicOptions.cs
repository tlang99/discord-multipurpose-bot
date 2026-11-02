namespace ModBot.Configuration;

public class MusicOptions
{
    public const string SectionName = "Music";

    /// <summary>Address of the Lavalink server that does the actual audio streaming. 127.0.0.1 rather than
    /// "localhost": Windows tries localhost over IPv6 first, and since Lavalink only listens on IPv4 that added ~2
    /// seconds to every request.</summary>
    public string LavalinkUrl { get; set; } = "http://127.0.0.1:2333";

    /// <summary>Must match <c>lavalink.server.password</c> in <c>lavalink/application.yml</c>. Override with the
    /// <c>LAVALINK_PASSWORD</c> environment variable or user-secrets rather than editing appsettings.json.</summary>
    public string LavalinkPassword { get; set; } = "youshallnotpass";

    /// <summary>How long the bot stays in a voice channel with nobody listening, or with nothing playing, before
    /// leaving.</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    /// <summary>Cap on the queue length per server, so one playlist can't queue thousands of songs.</summary>
    public int MaxQueueLength { get; set; } = 200;

    /// <summary>How many songs each person can have waiting at once, so everyone gets turns without one person's
    /// playlist filling the queue.</summary>
    public int SongsPerPerson { get; set; } = 10;

    /// <summary>Repost the music menu at the bottom of its channel when messages build up under it, so it stays in
    /// view. At most once a minute, and only after the channel's been quiet for a few seconds.</summary>
    public bool KeepMenuAtBottom { get; set; } = true;

    /// <summary>The only text channel music commands work in, by name (case-insensitive). Song announcements and the
    /// music menu go there too. Empty allows any channel.</summary>
    public string ChannelName { get; set; } = "music";
}
