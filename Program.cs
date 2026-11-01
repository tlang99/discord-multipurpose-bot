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
using ModBot.Services.ContentModeration;
using ModBot.Services.Moderation;
using ModBot.Services.News;
using ModBot.Services.Storage;

var builder = Host.CreateApplicationBuilder(args);

// Host.CreateApplicationBuilder already wires up appsettings.json,
// appsettings.{Environment}.json, environment variables, and command-line args.
// User secrets need to be added explicitly so a token works outside Development too.
builder.Configuration.AddUserSecrets<BotOptions>(optional: true);

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

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<SightengineOptions>(builder.Configuration.GetSection(SightengineOptions.SectionName));
builder.Services.Configure<NewsOptions>(builder.Configuration.GetSection(NewsOptions.SectionName));
builder.Services.Configure<MusicOptions>(builder.Configuration.GetSection(MusicOptions.SectionName));
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

builder.Services.AddSingleton<IModerationService, ModerationService>();

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

builder.Services.AddSingleton<AutoModerationHandler>();

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<NewsPollingService>();

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

await host.RunAsync();
