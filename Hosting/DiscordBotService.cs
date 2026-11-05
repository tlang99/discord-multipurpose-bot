using System.Collections.Concurrent;
using System.Diagnostics;
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
        _client.JoinedGuild += OnJoinedGuildAsync;
        _client.LeftGuild += OnLeftGuildAsync;
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
        // Which servers the bot is in, each time it connects, so a problem in one can be matched to its name and ID.
        _logger.LogInformation("Connected as {Bot} to {Count} server(s): {Servers}", _client.CurrentUser.Username,
            _client.Guilds.Count, string.Join(", ", _client.Guilds.OrderBy(g => g.Name).Select(Describe)));

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

    private Task OnJoinedGuildAsync(SocketGuild guild)
    {
        _logger.LogInformation("Added to server {Server}, owner {OwnerId}, {Members} members. Now in {Count} server(s).",
            Describe(guild), guild.OwnerId, guild.MemberCount, _client.Guilds.Count);
        return Task.CompletedTask;
    }

    private Task OnLeftGuildAsync(SocketGuild guild)
    {
        // Its saved settings are kept, so they're still there if it adds the bot back.
        _logger.LogInformation("Removed from server {Server}. Now in {Count} server(s).", Describe(guild),
            _client.Guilds.Count);
        return Task.CompletedTask;
    }

    /// <summary>"Server name (ID)" for the logs.</summary>
    private static string Describe(IGuild guild) => $"{guild.Name} ({guild.Id})";

    /// <summary>
    /// Tags every log line written while handling something from a server (a command, button, or message, including
    /// work they start in the background) with that server's name and ID, so a problem can be traced to the server it
    /// happened in. Shown on each line in the console as "=> Server name (ID)".
    /// </summary>
    private IDisposable? ServerScope(IGuild? guild)
        => guild is null ? null : _logger.BeginScope("{Server}", Describe(guild));

    /// <summary>When each interaction arrived, so the "done" line can say how long it took. Commands run in the
    /// background, so the start and the end are in different handlers.</summary>
    private readonly ConcurrentDictionary<ulong, long> _started = new();

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        using var scope = ServerScope((interaction.Channel as IGuildChannel)?.Guild);
        // Not suggestions (/lfg playlist as you type): they get no "done" line, so their start time would never go.
        if (interaction is not SocketAutocompleteInteraction)
        {
            _started[interaction.Id] = Stopwatch.GetTimestamp();
        }
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(context, _services);

            if (!result.IsSuccess)
            {
                // Failed before it started (unknown command, an old button), so no "done" line will take it out.
                _started.TryRemove(interaction.Id, out _);
                if (interaction.Type is InteractionType.ApplicationCommand)
                {
                    _logger.LogWarning("Interaction failed: {Error} - {Reason}", result.Error, result.ErrorReason);
                }
            }
        }
        catch (Exception ex)
        {
            _started.TryRemove(interaction.Id, out _);
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
        var took = _started.TryRemove(context.Interaction.Id, out var start)
            ? (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds
            : -1;
        var what = context.Interaction switch
        {
            ISlashCommandInteraction slash => $"/{slash.Data.Name}" +
                                              (command?.Name is { } sub && sub != slash.Data.Name ? $" {sub}" : ""),
            IComponentInteraction button => $"button {button.Data.CustomId}",
            IModalInteraction modal => $"pop-up {modal.Data.CustomId}",
            _ => command?.Name ?? "interaction",
        };

        // Successful use too, not just failures, so the log shows how each server uses the bot.
        if (result.IsSuccess)
        {
            _logger.LogInformation("{What} by {User} ({UserId}) in #{Channel} - done in {Took} ms.", what,
                context.User.Username, context.User.Id, (context.Channel as IGuildChannel)?.Name ?? "DM", took);
            return;
        }

        // Exceptions are also logged in full by the InteractionService's own Log event ("[App Commands]"); this line
        // adds who ran it and where. It already carries the server tag: it runs inside OnInteractionCreatedAsync's scope.
        var name = command?.Name ?? "unknown";
        _logger.Log(result.Error == InteractionCommandError.UnmetPrecondition ? LogLevel.Information : LogLevel.Warning,
            "/{Command} failed for user {UserId} in channel {ChannelId}: {Error} - {Reason}", name, context.User.Id,
            context.Channel?.Id, result.Error, result.ErrorReason);

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
        // Started inside the scope, so anything the handlers log carries the server.
        using var scope = ServerScope((message.Channel as IGuildChannel)?.Guild);
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

        // Voice encryption (DAVE) warns about every packet it can't decrypt yet while it's being set up, a few dozen
        // in the first second or two of listening. That's expected, not a problem: keep it out of the log files.
        if (level == LogLevel.Warning && message.Source.StartsWith("Dave", StringComparison.Ordinal))
        {
            level = LogLevel.Debug;
        }

        _logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
