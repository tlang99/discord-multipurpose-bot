using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Hosting;

/// <summary>
/// The Discord account that plays music. Discord gives each bot one voice connection per server, and listening for
/// "hey ModBot" needs the main bot's. With <c>Bot:MusicToken</c> set, a second bot account (its own application in
/// the Discord Developer Portal, invited to the server) joins voice for music instead, so music and listening can run
/// at once. Nobody talks to it: commands, the menu and the chatbot all stay on the main bot, which drives it through
/// Lavalink. Without the token, the main bot plays the music itself, and starting music stops the listening.
/// </summary>
public sealed class MusicBot
{
    /// <param name="separate">The music account's own client, or null to play through <paramref name="main"/>.</param>
    public MusicBot(DiscordSocketClient main, DiscordSocketClient? separate)
    {
        Client = separate ?? main;
        IsSeparate = separate is not null;
    }

    /// <summary>The client Lavalink joins voice with.</summary>
    public DiscordSocketClient Client { get; }

    /// <summary>Music plays from its own account, so it doesn't take the main bot's voice connection.</summary>
    public bool IsSeparate { get; }

    /// <summary>The music account in <paramref name="guildId"/>, or null if it isn't in that server (or hasn't
    /// connected yet).</summary>
    public SocketGuildUser? In(ulong guildId) => Client.GetGuild(guildId)?.CurrentUser;

    /// <summary>The link that adds the music account to a server, with what it needs in voice: View Channel, Connect
    /// and Speak.</summary>
    public string InviteLink
    {
        get
        {
            var permissions = new GuildPermissions(viewChannel: true, connect: true, speak: true).RawValue;
            return $"https://discord.com/oauth2/authorize?client_id={Client.CurrentUser?.Id}&scope=bot" +
                   $"&permissions={permissions}";
        }
    }

    /// <summary>The gateway settings for the music account: only what voice needs (servers and who's in which
    /// voice channel), none of the privileged intents.</summary>
    public static DiscordSocketConfig ClientConfig => new()
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
        LogLevel = LogSeverity.Info,
        UseInteractionSnowflakeDate = false,
    };
}

/// <summary>Signs the music account in and out, when there is one (see <see cref="MusicBot"/>).</summary>
public sealed class MusicBotService(MusicBot music, IOptions<BotOptions> options, ILogger<MusicBotService> logger)
    : IHostedService
{
    private readonly string _token = options.Value.MusicToken;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!music.IsSeparate)
        {
            return;
        }

        var client = music.Client;
        client.Log += message => DiscordBotService.Forward(logger, message);
        client.Ready += () =>
        {
            logger.LogInformation("Music plays from {Bot}, in {Count} server(s): {Servers}", client.CurrentUser.Username,
                client.Guilds.Count, string.Join(", ", client.Guilds.OrderBy(g => g.Name).Select(g => $"{g.Name} ({g.Id})")));
            return Task.CompletedTask;
        };

        try
        {
            await client.LoginAsync(TokenType.Bot, _token);
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            // Everything else still works; only music can't join voice.
            logger.LogError(ex, "Couldn't sign in the music bot (Bot:MusicToken), so music can't play. Check the " +
                                "token, or remove it to play music from the main bot.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (music.IsSeparate)
        {
            await music.Client.StopAsync();
            await music.Client.LogoutAsync();
        }
    }
}
