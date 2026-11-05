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
    Console.WriteLine($"Missing native libraries next to the exe: {string.Join(", ", missing)}. Run voice/setup.ps1.");
    return 1;
}

var modelPath = await EnsureModelAsync();

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMembers,
    AlwaysDownloadUsers = true,
    EnableVoiceDaveEncryption = true,
    LogLevel = LogSeverity.Debug,
});
client.Log += m =>
{
    // Everything from the voice connection (that's what's being tested), and only problems from the rest.
    if (m.Source.StartsWith("Audio", StringComparison.OrdinalIgnoreCase) || m.Severity <= LogSeverity.Warning)
    {
        Log($"[{m.Source}] {m.Severity}: {m.Message} {m.Exception?.Message}");
    }

    return Task.CompletedTask;
};

var audio = new ConcurrentDictionary<ulong, MemoryStream>(); // per speaker: 48 kHz stereo 16-bit PCM
var names = new ConcurrentDictionary<ulong, string>();
var done = new TaskCompletionSource<bool>();
var started = 0;
SocketVoiceChannel? joined = null;

// However the tool ends (Ctrl+C, closing the window), leave the channel and close the session properly. A killed
// session keeps the bot showing in the channel until Discord times it out.
async Task ShutDownAsync()
{
    if (Interlocked.Exchange(ref joined, null) is { } channel)
    {
        Log("Leaving the channel...");
        await Task.WhenAny(channel.DisconnectAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
    }

    await Task.WhenAny(client.StopAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log("Stopping...");
    done.TrySetResult(false);
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutDownAsync().Wait(TimeSpan.FromSeconds(10));

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
                Log($"Failed: {ex}");
                done.TrySetResult(false);
            }
        });
    }

    return Task.CompletedTask;
};

Log("Logging in...");
await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

// A hard limit, in case something hangs: never stay in the channel much longer than asked.
var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 60)));
var ok = finished == done.Task && done.Task.Result;
if (finished != done.Task)
{
    Log("Took far too long - giving up.");
}

var botName = client.CurrentUser?.Username ?? "ModBot"; // read before logging out, which clears it
await ShutDownAsync();
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
        Log("The bot isn't in that server, or that isn't a voice channel.");
        return false;
    }

    if (guild.CurrentUser.VoiceChannel is not null)
    {
        // Lavalink would lose its connection (it's the same bot), so don't take over a channel music is using.
        Log($"The bot is already in voice in {guild.Name} (#{guild.CurrentUser.VoiceChannel.Name}). " +
            "Stop the music there, or disconnect it, first.");
        return false;
    }

    Log($"Joining #{channel.Name} in {guild.Name}...");
    joined = channel;
    // Muted (it never talks) but not deafened, or Discord sends it nothing.
    var connecting = channel.ConnectAsync(selfDeaf: false, selfMute: true);
    if (await Task.WhenAny(connecting, Task.Delay(TimeSpan.FromSeconds(20))) != connecting)
    {
        Log("Joining never finished (20 seconds). The voice log lines above show how far it got.");
        return false;
    }

    var voice = await connecting;
    Log("Joined.");
    var stop = new CancellationTokenSource();
    var readers = new ConcurrentBag<Task>();
    var frames = new ConcurrentDictionary<ulong, int>();

    void Listen(ulong userId, AudioInStream? stream)
    {
        if (stream is null || guild.GetUser(userId) is not { IsBot: false } user)
        {
            return;
        }

        names[userId] = user.DisplayName;
        Log($"Listening to {user.DisplayName}.");
        readers.Add(Task.Run(async () =>
        {
            var buffer = audio.GetOrAdd(userId, _ => new MemoryStream());
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await stream.ReadFrameAsync(stop.Token);
                    frames.AddOrUpdate(userId, 1, (_, n) => n + 1);
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
    voice.SpeakingUpdated += (userId, speaking) =>
    {
        Log($"{guild.GetUser(userId)?.DisplayName ?? userId.ToString()} {(speaking ? "started" : "stopped")} talking.");
        return Task.CompletedTask;
    };
    foreach (var user in channel.ConnectedUsers)
    {
        Listen(user.Id, user.AudioStream);
    }

    Log($"Listening for {seconds} seconds - say something! (e.g. \"hey {guild.CurrentUser.DisplayName}, make an " +
        "LFG for ranked\")");
    for (var waited = 0; waited < seconds && !done.Task.IsCompleted; waited += 5)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds - waited)));
        // Audio arrives as 20 ms frames, so 50 a second while someone talks.
        Log("Audio so far: " + (frames.IsEmpty
            ? "none"
            : string.Join(", ", frames.Select(f => $"{names.GetValueOrDefault(f.Key, "?")} {f.Value / 50.0:0.0}s"))));
    }

    stop.Cancel();
    await Task.WhenAny(Task.WhenAll(readers), Task.Delay(TimeSpan.FromSeconds(3)));
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
            .WithPrompt($"Hey {botName}, LFG, Hardpoint, ranked.")
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

// Whisper's small English model (~140 MB), shared with the bot in voice/models (voice/setup.ps1 downloads it, or this does).
static async Task<string> EnsureModelAsync()
{
    var dir = Path.Combine(Directory.GetCurrentDirectory(), "voice", "models");
    var path = Path.Combine(dir, "ggml-base.en.bin");
    if (!File.Exists(path))
    {
        Directory.CreateDirectory(dir);
        Log("Downloading the speech-to-text model (once, ~140 MB)...");
        await using var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn);
        await using var file = File.Create(path);
        await model.CopyToAsync(file);
    }

    return path;
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
