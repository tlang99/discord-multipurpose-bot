using Discord;
using Discord.WebSocket;

namespace ModBot.Services.Music;

/// <summary>
/// The Discord account that plays music. Discord gives each bot one voice connection per server, and listening for
/// "hey ModBot" needs the main bot's. With <c>Bot:MusicToken</c> set, a second bot account (its own application in
/// the Discord Developer Portal, invited to the server) joins voice for music instead, so music and listening can run
/// at once. Nobody talks to it: commands, the menu and the chatbot all stay on the main bot, which drives it through
/// Lavalink. Without the token, the main bot plays the music itself, and starting music stops the listening.
/// Signed in and out by MusicBotService (in Hosting).
/// </summary>
public sealed class MusicBot : IDisposable
{
    private readonly DiscordSocketClient? _separate;

    /// <param name="separate">The music account's own client, or null to play through <paramref name="main"/>.</param>
    public MusicBot(DiscordSocketClient main, DiscordSocketClient? separate)
    {
        _separate = separate;
        Client = separate ?? main;
        if (separate is not null)
        {
            // Ready means its servers are loaded too: before that, it looks like it's in none of them.
            separate.Ready += () =>
            {
                _ready = true;
                return Task.CompletedTask;
            };
        }
    }

    private volatile bool _ready;

    /// <summary>The client Lavalink joins voice with.</summary>
    public DiscordSocketClient Client { get; }

    /// <summary>Music plays from its own account, so it doesn't take the main bot's voice connection.</summary>
    public bool IsSeparate => _separate is not null;

    /// <summary>The music account is connected and has loaded its servers. False while it's signing in or
    /// reconnecting, or if its token was refused.</summary>
    public bool IsSignedIn
        => _ready && Client.ConnectionState == ConnectionState.Connected && Client.CurrentUser is not null;

    /// <summary>The music account in <paramref name="guildId"/>, or null if it isn't in that server (or hasn't
    /// signed in).</summary>
    public SocketGuildUser? In(ulong guildId) => Client.GetGuild(guildId)?.CurrentUser;

    /// <summary>The link that adds the music account to a server, with what it needs in voice (View Channel, Connect
    /// and Speak); null until it has signed in, since the link needs its ID.</summary>
    public string? InviteLink
        => Client.CurrentUser is { Id: var id }
            ? $"https://discord.com/oauth2/authorize?client_id={id}&scope=bot&permissions={InvitePermissions.RawValue}"
            : null;

    /// <summary>What the music account is invited with: only what playing in voice needs.</summary>
    public static GuildPermissions InvitePermissions { get; } = new(viewChannel: true, connect: true, speak: true);

    /// <summary>The gateway settings for the music account: only what voice needs (servers and who's in which
    /// voice channel), none of the privileged intents.</summary>
    public static DiscordSocketConfig ClientConfig => new()
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
        LogLevel = LogSeverity.Info,
        UseInteractionSnowflakeDate = false,
    };

    /// <summary>Whether <paramref name="musicToken"/> makes a separate music account: set, and not the main bot's
    /// own token (two connections on one account would keep knocking each other off).</summary>
    public static bool IsSeparateToken(string? musicToken, string? mainToken)
        => !string.IsNullOrWhiteSpace(musicToken) && musicToken.Trim() != mainToken?.Trim();

    public void Dispose() => _separate?.Dispose();
}
