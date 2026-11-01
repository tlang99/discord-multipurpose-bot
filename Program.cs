using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<SightengineOptions>(builder.Configuration.GetSection(SightengineOptions.SectionName));
builder.Services.Configure<NewsOptions>(builder.Configuration.GetSection(NewsOptions.SectionName));

builder.Services.AddSingleton(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildMembers
        | GatewayIntents.GuildMessages
        | GatewayIntents.MessageContent
        | GatewayIntents.GuildBans,
    LogLevel = LogSeverity.Info,
    AlwaysDownloadUsers = true,
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

builder.Services.AddSingleton<AutoModerationHandler>();

builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<NewsPollingService>();

var host = builder.Build();
await host.RunAsync();
