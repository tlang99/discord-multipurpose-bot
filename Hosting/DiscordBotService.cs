using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
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
    private readonly ChatHandler _chatHandler;
    private readonly BotOptions _options;
    private readonly ILogger<DiscordBotService> _logger;

    public DiscordBotService(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        AutoModerationHandler autoModerationHandler,
        ChatHandler chatHandler,
        IOptions<BotOptions> options,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _autoModerationHandler = autoModerationHandler;
        _chatHandler = chatHandler;
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
        _interactions.InteractionExecuted += OnInteractionExecutedAsync;
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

    /// <summary>
    /// Commands run in the background (RunMode.Async), so ExecuteCommandAsync above reports success as soon as they're
    /// started - this is where a command that actually threw or failed a precondition ends up. Log it properly, and
    /// answer the interaction so the user sees an error instead of Discord's "The application did not respond".
    /// </summary>
    private async Task OnInteractionExecutedAsync(ICommandInfo? command, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess)
        {
            return;
        }

        // Exceptions are already logged in full by the InteractionService's own Log event ("[App Commands]").
        var name = command?.Name ?? "unknown";
        if (result.Error != InteractionCommandError.Exception)
        {
            _logger.LogWarning("/{Command} failed: {Error} - {Reason}", name, result.Error, result.ErrorReason);
        }

        var message = result.Error switch
        {
            InteractionCommandError.UnmetPrecondition => result.ErrorReason,
            InteractionCommandError.Exception => "Something went wrong running that command. The error has been logged.",
            _ => "That command couldn't be run.",
        };

        try
        {
            if (context.Interaction.HasResponded)
            {
                await context.Interaction.FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            else
            {
                await context.Interaction.RespondAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
            }
        }
        catch (Exception ex)
        {
            // Most likely the interaction already expired (more than 15 minutes, or never acknowledged in 3 seconds).
            _logger.LogDebug(ex, "Couldn't send the error reply for /{Command}.", name);
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        Background.Run(_logger, () => _autoModerationHandler.HandleMessageAsync(message), LogLevel.Error,
            "Unhandled exception in auto-moderation handler.");

        // Separately, since waiting on the AI can take many seconds and mustn't hold up auto-mod.
        Background.Run(_logger, () => _chatHandler.HandleMessageAsync(message), LogLevel.Error,
            "Unhandled exception in chat handler.");

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
