using Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Music;

namespace ModBot.Hosting;

/// <summary>Signs the music account in and out, when there is one (see <see cref="MusicBot"/>).</summary>
public sealed class MusicBotService(MusicBot music, IOptions<BotOptions> options, ILogger<MusicBotService> logger)
    : IHostedService
{
    private readonly BotOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!music.IsSeparate)
        {
            if (!string.IsNullOrWhiteSpace(_options.MusicToken))
            {
                logger.LogWarning("Bot:MusicToken is the main bot's own token, so it's ignored: music plays from the " +
                                  "main bot. Give it a second bot account's token to play music while listening.");
            }

            return;
        }

        var client = music.Client;
        client.Log += message => DiscordBotService.Forward(logger, message);
        client.Ready += () =>
        {
            logger.LogInformation("Music plays from {Bot}, in {Count} server(s): {Servers}", client.CurrentUser.Username,
                client.Guilds.Count,
                string.Join(", ", client.Guilds.OrderBy(g => g.Name).Select(g => $"{g.Name} ({g.Id})")));
            return Task.CompletedTask;
        };

        try
        {
            await client.LoginAsync(TokenType.Bot, _options.MusicToken.Trim()); // env vars often end in a newline
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            // Everything else still works; only music can't join voice (and /play says so).
            logger.LogError(ex, "Couldn't sign in the music bot (Bot:MusicToken), so music can't play. Check the " +
                                "token, or remove it to play music from the main bot.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!music.IsSeparate)
        {
            return;
        }

        try
        {
            await music.Client.StopAsync();
            await music.Client.LogoutAsync();
        }
        catch (Exception ex)
        {
            // Shutting down anyway (and it may never have signed in): nothing to stop for.
            logger.LogDebug(ex, "Couldn't sign the music bot out cleanly.");
        }
    }
}
