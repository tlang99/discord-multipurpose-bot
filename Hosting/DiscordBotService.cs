using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.EventHandlers;

namespace ModBot.Hosting;

/// <summary>
/// Owns the Discord client lifecycle: login, slash-command module discovery
/// and registration, and wiring gateway events to the relevant handlers.
/// New gateway-driven features (e.g. a welcome-message handler) should be
/// added as their own class under EventHandlers/ and wired up here.
/// </summary>
public class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly AutoModerationHandler _autoModerationHandler;
    private readonly BotOptions _options;
    private readonly ILogger<DiscordBotService> _logger;

    public DiscordBotService(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        AutoModerationHandler autoModerationHandler,
        IOptions<BotOptions> options,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _autoModerationHandler = autoModerationHandler;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _logger.LogCritical(
                "No bot token configured. Set it via `dotnet user-secrets set \"Bot:Token\" \"<token>\"` " +
                "or the BOT_TOKEN/Bot__Token environment variable.");
            return;
        }

        _client.Log += LogAsync;
        _interactions.Log += LogAsync;
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;

        await _interactions.AddModulesAsync(Assembly.GetExecutingAssembly(), _services);

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected on shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task OnReadyAsync()
    {
        try
        {
            if (_options.TestGuildId is ulong guildId)
            {
                await _interactions.RegisterCommandsToGuildAsync(guildId);
                _logger.LogInformation("Slash commands registered to test guild {GuildId}.", guildId);
            }
            else
            {
                await _interactions.RegisterCommandsGloballyAsync();
                _logger.LogInformation("Slash commands registered globally.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands.");
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(context, _services);

            if (!result.IsSuccess && interaction.Type is InteractionType.ApplicationCommand)
            {
                _logger.LogWarning("Interaction failed: {Error} - {Reason}", result.Error, result.ErrorReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while executing an interaction.");
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _autoModerationHandler.HandleMessageAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in auto-moderation handler.");
            }
        });

        return Task.CompletedTask;
    }

    private Task LogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
