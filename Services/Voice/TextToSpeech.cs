using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Services.Voice;

/// <summary>
/// Turns text into speech on this machine with Piper (voice/README.md), for reading answers to spoken requests aloud.
/// Like speech-to-text, nothing leaves the machine, and one at a time: each takes a moment of CPU.
/// </summary>
public sealed class TextToSpeech : IDisposable
{
    /// <summary>The longest Piper gets to say something; past this it's stopped (it would be a very long answer, or
    /// stuck).</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Piper's own default, for a voice whose settings don't say.</summary>
    private const int DefaultSampleRate = 22_050;

    private readonly VoiceOptions _options;
    private readonly string _piperPath;
    private readonly string _voicePath;
    private readonly ILogger<TextToSpeech> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lazy<string?> _problem;
    private readonly Lazy<int> _sampleRate;

    public TextToSpeech(IOptions<VoiceOptions> options, IHostEnvironment environment, ILogger<TextToSpeech> logger)
    {
        _options = options.Value;
        _piperPath = Path.GetFullPath(PiperProgram(_options.PiperPath, OperatingSystem.IsWindows()),
            environment.ContentRootPath);
        _voicePath = Path.GetFullPath(_options.SpeakingVoicePath, environment.ContentRootPath);
        _logger = logger;
        _problem = new Lazy<string?>(FindProblem);
        _sampleRate = new Lazy<int>(ReadSampleRate);
    }

    /// <summary>Piper's program as configured, for this system: Linux's Piper is plain "piper", so a Windows path
    /// ("voice/piper/piper.exe", the default and what a copied appsettings.json says) means that there.</summary>
    public static string PiperProgram(string configured, bool windows)
        => !windows && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured[..^4] : configured;

    /// <summary>Why answers can't be spoken on this bot (for the admin running it), or null if they can.</summary>
    public string? Problem => _problem.Value;

    /// <summary>
    /// <paramref name="text"/> spoken, in Discord's audio format (see <see cref="VoiceAudio.FromSpeech"/>), or null
    /// if it couldn't be (logged). Give it text that's already fit to speak (<see cref="SpokenText"/>).
    /// </summary>
    public async Task<byte[]?> SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (Problem is not null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        await _one.WaitAsync(cancellationToken);
        try
        {
            var raw = await RunPiperAsync(text, cancellationToken);
            return raw is null
                ? null
                : VoiceAudio.FromSpeech(raw, _sampleRate.Value, VoiceAudio.Share(_options.SpeechVolumePercent));
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Piper's raw output for <paramref name="text"/> (16-bit mono), or null if it failed.</summary>
    private async Task<byte[]?> RunPiperAsync(string text, CancellationToken cancellationToken)
    {
        // The text goes in on standard input, never on the command line, so nothing in it can be read as an option.
        var start = new ProcessStartInfo(_piperPath)
        {
            ArgumentList = { "--model", _voicePath, "--output_raw" },
            WorkingDirectory = Path.GetDirectoryName(_piperPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            process.Start();
            // Read both outputs while it runs: a full pipe would make Piper wait forever. Its log is only for
            // troubleshooting and never holds the text.
            var audio = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(audio, timeout.Token);
            var log = process.StandardError.ReadToEndAsync(timeout.Token);

            // One line: Piper reads a line at a time, and would otherwise speak each line of the answer separately.
            await process.StandardInput.WriteLineAsync(text.ReplaceLineEndings(" ").AsMemory(), timeout.Token);
            process.StandardInput.Close();

            await Task.WhenAll(reading, log);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || audio.Length == 0)
            {
                _logger.LogWarning("Text-to-speech failed (Piper exit code {Code}): {Log}", process.ExitCode,
                    LastLine(await log));
                return null;
            }

            return audio.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Text-to-speech took longer than {Seconds} s, so it was stopped.", Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't run Piper for text-to-speech.");
            return null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Never started, or already gone.
            }
        }
    }

    private static string LastLine(string log)
        => log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private string? FindProblem()
    {
        if (!_options.Enabled || !_options.SpokenReplies)
        {
            return $"Spoken replies are turned off in the bot's settings " +
                   $"({(_options.Enabled ? "Voice:SpokenReplies" : "Voice:Enabled")}).";
        }

        if (!File.Exists(_piperPath))
        {
            return $"Piper (text-to-speech) isn't at {_options.PiperPath}: run voice/setup.ps1 (see voice/README.md).";
        }

        return File.Exists(_voicePath) && File.Exists(_voicePath + ".json")
            ? null
            : $"The speaking voice isn't at {_options.SpeakingVoicePath} (with its .onnx.json): run voice/setup.ps1.";
    }

    /// <summary>The voice's sample rate, from the settings file that comes with it.</summary>
    private int ReadSampleRate()
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllBytes(_voicePath + ".json"));
            if (settings.RootElement.TryGetProperty("audio", out var audio) &&
                audio.TryGetProperty("sample_rate", out var rate) && rate.TryGetInt32(out var hz) &&
                hz is >= 8_000 and <= 48_000) // what voices use; anything else is a broken settings file
            {
                return hz;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read the speaking voice's settings; assuming {Rate} Hz.", DefaultSampleRate);
        }

        return DefaultSampleRate;
    }

    public void Dispose() => _one.Dispose();
}
