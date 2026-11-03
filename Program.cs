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

// Show the "=> Server name (ID)" tag DiscordBotService puts on every log line from a server.
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);

// Also keep the log in daily files (logs/modbot-<date>.log, the last Logging:File:RetainDays days, default 5).
builder.Logging.AddProvider(new FileLoggerProvider(
    builder.Configuration.GetSection("Logging:File").Get<FileLoggerOptions>() ?? new FileLoggerOptions()));

// Every web request logs four lines and the music library logs every player change, which buries the bot's own
// lines. Only their warnings and errors are kept, in the console and the file alike.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Lavalink4NET", LogLevel.Warning);

// appsettings.json is not committed (it holds each person's own settings); the template is appsettings.example.json.
// Without it the bot still runs on the built-in defaults, but say so in case the copy step was missed.
var missingAppSettings = !File.Exists(Path.Combine(builder.Environment.ContentRootPath, "appsettings.json"));

// Plain env vars as a convenient alternative to the Section__Key syntax. OLLAMA_API_KEY is Ollama's own name for it,
// so a key already set up for the Ollama CLI works here too.
foreach (var (variable, key) in new[]
{
    ("BOT_TOKEN", $"{BotOptions.SectionName}:Token"),
    ("SIGHTENGINE_API_USER", $"{SightengineOptions.SectionName}:ApiUser"),
    ("SIGHTENGINE_API_SECRET", $"{SightengineOptions.SectionName}:ApiSecret"),
    ("LAVALINK_PASSWORD", $"{MusicOptions.SectionName}:LavalinkPassword"),
    ("OLLAMA_API_KEY", $"{ChatOptions.SectionName}:ApiKey"),
})
{
    if (Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value))
    {
        builder.Configuration[key] = value;
    }
}

T BindOptions<T>(string section) where T : class, new()
{
    builder.Services.Configure<T>(builder.Configuration.GetSection(section));
    return builder.Configuration.GetSection(section).Get<T>() ?? new T();
}

BindOptions<BotOptions>(BotOptions.SectionName);
var chatOptions = BindOptions<ChatOptions>(ChatOptions.SectionName);
BindOptions<SightengineOptions>(SightengineOptions.SectionName);
BindOptions<NewsOptions>(NewsOptions.SectionName);
var musicOptions = BindOptions<MusicOptions>(MusicOptions.SectionName);
BindOptions<LfgOptions>(LfgOptions.SectionName);
BindOptions<ReminderOptions>(ReminderOptions.SectionName);
BindOptions<PollOptions>(PollOptions.SectionName);

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
builder.Services.AddSingleton<ModBot.Services.Reminders.IReminderSender, DiscordReminderSender>();
builder.Services.AddSingleton<ModBot.Services.Reminders.ReminderDeliverer>();
builder.Services.AddSingleton<IChatSettingsStore, JsonChatSettingsStore>();

// Each server's own choices (/settings): which features are on, and their channels and LFG role. appsettings.json
// holds the defaults for servers that haven't changed them.
builder.Services.AddSingleton<IServerSettingsStore, JsonServerSettingsStore>();
builder.Services.AddSingleton<ModBot.Services.Settings.ServerSettingsService>();
builder.Services.AddSingleton<ModBot.Modules.Settings.ServerFeatureSwitch>();
builder.Services.AddSingleton<ModBot.Services.News.NewsNotifications>();
builder.Services.AddSingleton<ModBot.Modules.Polls.PollPoster>();

builder.Services.AddSingleton<IModerationService, ModerationService>();

// LFG posts are held in memory only (see ILfgService), so this is the single source of truth while the bot runs.
builder.Services.AddSingleton<ILfgService, LfgService>();
builder.Services.AddSingleton<ModBot.Modules.Lfg.LfgPoster>();
builder.Services.AddSingleton<ModBot.Modules.Music.MusicPlayerEvents>();
builder.Services.AddSingleton<ModBot.Modules.Music.MusicService>();
builder.Services.AddSingleton<IMusicQueueStore, JsonMusicQueueStore>();
builder.Services.AddSingleton<ModBot.Modules.Music.MusicQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModBot.Modules.Music.MusicQueueService>());
builder.Services.AddSingleton<ModBot.Modules.Music.MusicControls>();

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

void WarnAtStartup(string category, string message, params object?[] args)
#pragma warning disable CA2254 // Every message is a constant below.
    => host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category).LogWarning(message, args);
#pragma warning restore CA2254

// The default Lavalink password is only acceptable while Lavalink is reachable from this machine alone. Pointed at
// another machine (or a Docker port), it would leave the music server's control channel open over plain HTTP.
if (musicOptions.LavalinkPassword == new MusicOptions().LavalinkPassword &&
    Uri.TryCreate(musicOptions.LavalinkUrl, UriKind.Absolute, out var lavalinkUri) && !lavalinkUri.IsLoopback)
{
    WarnAtStartup("ModBot.Music",
        "Lavalink at {Url} is not on this machine but still uses the default password. Set the same new password " +
        "with LAVALINK_SERVER_PASSWORD for Lavalink and LAVALINK_PASSWORD for the bot (see lavalink/README.md).",
        musicOptions.LavalinkUrl);
}

// Music:ChannelName and Lfg:ChannelName were removed when servers got /settings channel. An old appsettings.json that
// still has them would otherwise be ignored without a word, leaving music and LFG open in every channel.
foreach (var oldKey in new[] { "Music:ChannelName", "Lfg:ChannelName" })
{
    if (!string.IsNullOrWhiteSpace(builder.Configuration[oldKey]))
    {
        WarnAtStartup("ModBot", "{Key} in appsettings.json is no longer used: each server picks its own channel with " +
                                "/settings channel (until then, any channel works). Remove the key to hide this.", oldKey);
    }
}

if (missingAppSettings)
{
    WarnAtStartup("ModBot",
        "No appsettings.json found in {Path}, so the built-in defaults are used. Copy appsettings.example.json to " +
        "appsettings.json to change settings.", builder.Environment.ContentRootPath);
}

if (chatOptions.IsEnabled &&
    Uri.TryCreate(chatOptions.OllamaUrl, UriKind.Absolute, out var ollamaUrl) &&
    ollamaUrl.Host.EndsWith("ollama.com", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(chatOptions.ApiKey))
{
    WarnAtStartup("ModBot.Chat",
        "The chatbot uses {Url} but no API key is set, so every reply will fail. Create one at " +
        "https://ollama.com/settings/keys and set it with OLLAMA_API_KEY (see README).", chatOptions.OllamaUrl);
}

await host.RunAsync();
