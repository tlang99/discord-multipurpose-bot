using System.Collections.Concurrent;
using Discord;
using Discord.Audio;
using Discord.LibDave;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Whisper.net;
using Whisper.net.Ggml;

// Voice commands, step one: can the bot hear a voice channel (through Discord's DAVE end-to-end encryption) and turn
// what people say into text, all on this PC? Joins the channel, listens for a while, leaves, and prints what each
// person said. Nothing is saved: the audio stays in memory and is gone when the tool exits.
//
//   dotnet run --project tools/VoiceTest -- <server id> <voice channel id> [seconds]
//
// Run it from the repo folder so it finds appsettings.json. See README.md.

if (args.Length < 2 || !ulong.TryParse(args[0], out var guildId) || !ulong.TryParse(args[1], out var channelId))
{
    Console.WriteLine("Usage: dotnet run --project tools/VoiceTest -- <server id> <voice channel id> [seconds]");
    return 1;
}

var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? Math.Clamp(s, 5, 120) : 20;

// The bot's token, from wherever the bot itself reads it.
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true)
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();
var token = config["BOT_TOKEN"] is { Length: > 0 } fromEnv ? fromEnv : config["Bot:Token"];
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("No bot token found (appsettings.json Bot:Token, user secrets, or BOT_TOKEN).");
    return 1;
}

// The native libraries Discord.Net needs for voice. Without libdave the bot can't join at all: Discord requires DAVE.
var missing = new[] { "libdave.dll", "libsodium.dll", "opus.dll" }
    .Where(f => !File.Exists(Path.Combine(AppContext.BaseDirectory, f))).ToList();
if (missing.Count > 0 || !Dave.CheckAvailability())
{
    Console.WriteLine($"Missing native libraries next to the exe: {string.Join(", ", missing)}. See tools/VoiceTest/README.md.");
    return 1;
}

var modelPath = await EnsureModelAsync();

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMembers,
    AlwaysDownloadUsers = true,
    EnableVoiceDaveEncryption = true,
    LogLevel = LogSeverity.Warning,
});
client.Log += m =>
{
    Console.WriteLine($"[discord] {m.Severity}: {m.Message} {m.Exception?.Message}");
    return Task.CompletedTask;
};

var audio = new ConcurrentDictionary<ulong, MemoryStream>(); // per speaker: 48 kHz stereo 16-bit PCM
var names = new ConcurrentDictionary<ulong, string>();
var done = new TaskCompletionSource<bool>();
var started = 0;

client.Ready += () =>
{
    // Off the gateway's thread: joining voice waits on gateway events, so doing it here would deadlock.
    if (Interlocked.Exchange(ref started, 1) == 0)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                done.TrySetResult(await ListenAsync());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex}");
                done.TrySetResult(false);
            }
        });
    }

    return Task.CompletedTask;
};

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
var ok = await done.Task;
await client.StopAsync();
await client.LogoutAsync();

if (!ok)
{
    return 1;
}

await TranscribeAsync();
return 0;

async Task<bool> ListenAsync()
{
    var guild = client.GetGuild(guildId);
    if (guild?.GetVoiceChannel(channelId) is not { } channel)
    {
        Console.WriteLine("The bot isn't in that server, or that isn't a voice channel.");
        return false;
    }

    if (guild.CurrentUser.VoiceChannel is not null)
    {
        // Lavalink would lose its connection (it's the same bot), so don't take over a channel music is using.
        Console.WriteLine($"The bot is already in voice in {guild.Name} (#{guild.CurrentUser.VoiceChannel.Name}). " +
                          "Stop the music there first.");
        return false;
    }

    Console.WriteLine($"Joining #{channel.Name} in {guild.Name}...");
    // Muted (it never talks) but not deafened, or Discord sends it nothing.
    var voice = await channel.ConnectAsync(selfDeaf: false, selfMute: true);
    var stop = new CancellationTokenSource();
    var readers = new ConcurrentBag<Task>();

    void Listen(ulong userId, AudioInStream? stream)
    {
        if (stream is null || guild.GetUser(userId) is not { IsBot: false } user)
        {
            return;
        }

        names[userId] = user.DisplayName;
        readers.Add(Task.Run(async () =>
        {
            var buffer = audio.GetOrAdd(userId, _ => new MemoryStream());
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await stream.ReadFrameAsync(stop.Token);
                    lock (buffer)
                    {
                        buffer.Write(frame.Payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }));
    }

    voice.StreamCreated += (userId, stream) =>
    {
        Listen(userId, stream);
        return Task.CompletedTask;
    };
    foreach (var user in channel.ConnectedUsers)
    {
        Listen(user.Id, user.AudioStream);
    }

    Console.WriteLine($"Listening for {seconds} seconds - say something! (e.g. \"hey {guild.CurrentUser.DisplayName}, " +
                      "make an LFG for ranked\")");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    stop.Cancel();
    await Task.WhenAll(readers);
    await channel.DisconnectAsync();
    Console.WriteLine("Left the channel.");
    return true;
}

async Task TranscribeAsync()
{
    var heard = audio.Where(a => a.Value.Length > 0).ToList();
    if (heard.Count == 0)
    {
        Console.WriteLine("\nHeard nothing. If people were talking, the audio didn't get through (DAVE, or the bot " +
                          "was deafened).");
        return;
    }

    using var factory = WhisperFactory.FromPath(modelPath);
    foreach (var (userId, pcm) in heard)
    {
        var samples = ToWhisperSamples(pcm.ToArray());
        var started = DateTime.UtcNow;
        using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            // Words it should expect, so the wake word and game terms come out spelled right.
            .WithPrompt($"Hey {client.CurrentUser.Username}, LFG, Hardpoint, ranked.")
            .Build();

        var text = new List<string>();
        await foreach (var segment in processor.ProcessAsync(samples))
        {
            text.Add(segment.Text.Trim());
        }

        Console.WriteLine($"\n{names[userId]} ({samples.Length / 16000.0:0.0}s of speech, transcribed in " +
                          $"{(DateTime.UtcNow - started).TotalSeconds:0.0}s):\n  {string.Join(" ", text)}");
    }
}

// Discord gives 48 kHz stereo 16-bit samples; Whisper wants 16 kHz mono floats. Each output sample is the average of
// three input samples (both channels), which also filters out what 16 kHz can't hold.
static float[] ToWhisperSamples(byte[] pcm)
{
    var frames = pcm.Length / 4; // 2 channels x 2 bytes
    var output = new float[frames / 3];
    for (var i = 0; i < output.Length; i++)
    {
        var sum = 0;
        for (var j = 0; j < 3; j++)
        {
            var at = (i * 3 + j) * 4;
            sum += BitConverter.ToInt16(pcm, at) + BitConverter.ToInt16(pcm, at + 2);
        }

        output[i] = sum / (6f * 32768f);
    }

    return output;
}

// Whisper's small English model (~140 MB), downloaded once into tools/VoiceTest/models (not committed).
static async Task<string> EnsureModelAsync()
{
    var dir = Path.Combine(Directory.GetCurrentDirectory(), "tools", "VoiceTest", "models");
    var path = Path.Combine(dir, "ggml-base.en.bin");
    if (!File.Exists(path))
    {
        Directory.CreateDirectory(dir);
        Console.WriteLine("Downloading the speech-to-text model (once, ~140 MB)...");
        await using var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn);
        await using var file = File.Create(path);
        await model.CopyToAsync(file);
    }

    return path;
}
