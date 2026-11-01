using System.Net.Http.Headers;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Lavalink4NET.Extensions;
using Lavalink4NET.InactivityTracking.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModBot.Configuration;
using ModBot.EventHandlers;
using ModBot.Hosting;
using ModBot.Services.Chat;
using ModBot.Services.ContentModeration;
using ModBot.Services.Lfg;
using ModBot.Services.Moderation;
using ModBot.Services.News;
using ModBot.Services.Storage;

var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder already wires up appsettings.json,
// appsettings.{Environment}.json, environment variables, and command-line args.
// User secrets need to be added explicitly so a token works outside Development too.
builder.Configuration.AddUserSecrets<BotOptions>(optional: true);

// appsettings.json is not committed (it holds each person's own settings); the template is appsettings.example.json.
// Without it the bot still runs on the built-in defaults, but say so in case the copy step was missed.
var missingAppSettings = !File.Exists(Path.Combine(builder.Environment.ContentRootPath, "appsettings.json"));

// Allow plain env vars as a convenient alternative to the Section__Key syntax.
var envToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
if (!string.IsNullOrWhiteSpace(envToken))
{
    builder.Configuration[$"{BotOptions.SectionName}:Token"] = envToken;
}

var envSightengineUser = Environment.GetEnvironmentVariable("SIGHTENGINE_API_USER");
if (!string.IsNullOrWhiteSpace(envSightengineUser))
{
    builder.Configuration[$"{SightengineOptions.SectionName}:ApiUser"] = envSightengineUser;
}

var envSightengineSecret = Environment.GetEnvironmentVariable("SIGHTENGINE_API_SECRET");
if (!string.IsNullOrWhiteSpace(envSightengineSecret))
{
    builder.Configuration[$"{SightengineOptions.SectionName}:ApiSecret"] = envSightengineSecret;
}

var envLavalinkPassword = Environment.GetEnvironmentVariable("LAVALINK_PASSWORD");
if (!string.IsNullOrWhiteSpace(envLavalinkPassword))
{
    builder.Configuration[$"{MusicOptions.SectionName}:LavalinkPassword"] = envLavalinkPassword;
}

// Ollama's own name for the variable, so a key already set up for the Ollama CLI works here too.
var envOllamaKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
if (!string.IsNullOrWhiteSpace(envOllamaKey))
{
    builder.Configuration[$"{ChatOptions.SectionName}:ApiKey"] = envOllamaKey;
}

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection(ChatOptions.SectionName));
builder.Services.Configure<SightengineOptions>(builder.Configuration.GetSection(SightengineOptions.SectionName));
builder.Services.Configure<NewsOptions>(builder.Configuration.GetSection(NewsOptions.SectionName));
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection(MusicOptions.SectionName));
builder.Services.Configure<LfgOptions>(builder.Configuration.GetSection(LfgOptions.SectionName));
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection(ReminderOptions.SectionName));
var musicOptions = builder.Configuration.GetSection(MusicOptions.SectionName).Get<MusicOptions>() ?? new MusicOptions();

builder.Services.AddSingleton(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildMembers
        | GatewayIntents.GuildMessages
        | GatewayIntents.MessageContent
        | GatewayIntents.GuildBans
        | GatewayIntents.GuildVoiceStates, // music: who's in which voice channel
    LogLevel = LogSeverity.Info,
    AlwaysDownloadUsers = true,
    // Time the 3-second interaction window from when the bot received the interaction, not from Discord's creation
    // timestamp compared to this PC's clock. With the clock running a few seconds fast, the default makes every
    // command look expired on arrival ("Cannot defer an interaction after 3 seconds!").
    UseInteractionSnowflakeDate = false,
});

builder.Services.AddSingleton<DiscordSocketClient>();

builder.Services.AddSingleton(new InteractionServiceConfig
{
    DefaultRunMode = RunMode.Async,
});

builder.Services.AddSingleton(provider => new InteractionService(
    provider.GetRequiredService<DiscordSocketClient>(),
    provider.GetRequiredService<InteractionServiceConfig>()));

// Storage - swap these registrations for database-backed implementations later
// without touching anything that depends on IGuildSettingsStore / IWarningStore.
builder.Services.AddSingleton<IGuildSettingsStore, JsonGuildSettingsStore>();
builder.Services.AddSingleton<IWarningStore, JsonWarningStore>();
builder.Services.AddSingleton<INewsSubscriptionStore, JsonNewsSubscriptionStore>();
builder.Services.AddSingleton<IReminderStore, JsonReminderStore>();
builder.Services.AddSingleton<ModBot.Services.Reminders.ReminderService>();
builder.Services.AddSingleton<IChatSettingsStore, JsonChatSettingsStore>();

builder.Services.AddSingleton<IModerationService, ModerationService>();

// LFG posts are held in memory only (see ILfgService), so this is the single source of truth while the bot runs.
builder.Services.AddSingleton<ILfgService, LfgService>();
builder.Services.AddSingleton<ModBot.Modules.Lfg.LfgPoster>();
builder.Services.AddSingleton<ModBot.Modules.Music.MusicService>();

builder.Services.AddHttpClient<IImageModerationService, SightengineImageModerationService>(client =>
{
    client.BaseAddress = new Uri("https://api.sightengine.com/1.0/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<INewsFeedReader, RssNewsFeedReader>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    // Some feed hosts reject requests with no User-Agent.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ModBot/1.0 (Discord news bot)");
})
// Feed URLs come from server admins, so only allow connections to the public internet.
.ConfigurePrimaryHttpMessageHandler(PublicNetworkHttpHandler.Create);

// Music: Lavalink (a separate Java server, see lavalink/README.md) does the streaming; Lavalink4NET talks to it
// and registers its own hosted services. If Lavalink isn't running, only the music commands fail.
builder.Services.AddLavalink();
builder.Services.ConfigureLavalink(options =>
{
    options.BaseAddress = new Uri(musicOptions.LavalinkUrl);
    options.Passphrase = musicOptions.LavalinkPassword;
});

// Leave the voice channel once nobody's listening or nothing has played for a while.
builder.Services.AddInactivityTracking();
builder.Services.ConfigureInactivityTracking(options =>
{
    options.DefaultTimeout = TimeSpan.FromSeconds(Math.Max(10, musicOptions.IdleTimeoutSeconds));
});

// Chatbot: Ollama (ollama.com or self-hosted, see Chat:OllamaUrl) behind IChatService.
var chatOptions = builder.Configuration.GetSection(ChatOptions.SectionName).Get<ChatOptions>() ?? new ChatOptions();
builder.Services.AddHttpClient<IChatService, OllamaChatService>(client =>
{
    if (Uri.TryCreate(chatOptions.OllamaUrl.TrimEnd('/') + "/", UriKind.Absolute, out var ollamaUri))
    {
        client.BaseAddress = ollamaUri;
    }

    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(chatOptions.TimeoutSeconds, 5, 300));
    if (!string.IsNullOrWhiteSpace(chatOptions.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", chatOptions.ApiKey);
    }
});

builder.Services.AddSingleton<AutoModerationHandler>();
builder.Services.AddSingleton<ChatRequestLimiter>();
builder.Services.AddSingleton<ChatPersonality>();
builder.Services.AddSingleton<ChatActionParser>();
builder.Services.AddSingleton<INewsWriter, NewsWriter>();
builder.Services.AddSingleton<ChatHandler>();

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<NewsPollingService>();
builder.Services.AddHostedService<LfgExpiryService>();
builder.Services.AddHostedService<ReminderDeliveryService>();

var host = builder.Build();

// The default Lavalink password is only acceptable while Lavalink is reachable from this machine alone. Pointed at
// another machine (or a Docker port), it would leave the music server's control channel open over plain HTTP.
if (musicOptions.LavalinkPassword == new MusicOptions().LavalinkPassword &&
    Uri.TryCreate(musicOptions.LavalinkUrl, UriKind.Absolute, out var lavalinkUri) && !lavalinkUri.IsLoopback)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModBot.Music").LogWarning(
        "Lavalink at {Url} is not on this machine but still uses the default password. Set the same new password " +
        "with LAVALINK_SERVER_PASSWORD for Lavalink and LAVALINK_PASSWORD for the bot (see lavalink/README.md).",
        musicOptions.LavalinkUrl);
}

if (missingAppSettings)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModBot").LogWarning(
        "No appsettings.json found in {Path}, so the built-in defaults are used. Copy appsettings.example.json to " +
        "appsettings.json to change settings.", builder.Environment.ContentRootPath);
}

if (chatOptions.IsEnabled &&
    Uri.TryCreate(chatOptions.OllamaUrl, UriKind.Absolute, out var ollamaUrl) &&
    ollamaUrl.Host.EndsWith("ollama.com", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(chatOptions.ApiKey))
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModBot.Chat").LogWarning(
        "The chatbot uses {Url} but no API key is set, so every reply will fail. Create one at " +
        "https://ollama.com/settings/keys and set it with OLLAMA_API_KEY (see README).", chatOptions.OllamaUrl);
}

await host.RunAsync();
