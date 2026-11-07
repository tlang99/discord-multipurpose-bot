using Discord.LibDave;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using Whisper.net;

namespace ModBot.Services.Voice;

/// <summary>
/// Turns speech into text on this machine with Whisper, and says whether voice commands can work here at all (the
/// model and Discord's native voice libraries have to be in place, see voice/README.md). One transcription at a time:
/// each takes about a second of CPU, and running several at once would only slow them all down.
/// </summary>
public sealed class SpeechToText : IDisposable
{
    /// <summary>Native libraries Discord.Net loads from the bot's folder for voice: Discord's DAVE encryption, the
    /// transport encryption, and the Opus audio codec.</summary>
    private static readonly string[] NativeLibraries = ["libdave", "libsodium", "opus"];

    private readonly VoiceOptions _options;
    private readonly string _modelPath;
    private readonly ILogger<SpeechToText> _logger;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lazy<string?> _problem;
    private WhisperFactory? _factory;

    public SpeechToText(IOptions<VoiceOptions> options, IHostEnvironment environment, ILogger<SpeechToText> logger)
    {
        _options = options.Value;
        _modelPath = Path.GetFullPath(_options.ModelPath, environment.ContentRootPath);
        _logger = logger;
        _problem = new Lazy<string?>(FindProblem);
    }

    /// <summary>Why voice commands can't work on this bot (for the admin running it), or null if they can.</summary>
    public string? Problem => _loadProblem ?? _problem.Value;

    /// <summary>Set when the model was there but wouldn't load (e.g. a damaged download).</summary>
    private string? _loadProblem;

    /// <summary>A stretch at least this likely to hold no speech is noise, whatever words came out of it.</summary>
    private const float NoSpeechCutoff = 0.6f;

    /// <summary>What was said, or "" if nothing could be made out.</summary>
    /// <param name="samples">16 kHz mono (see <see cref="VoiceAudio.ToWhisperSamples"/>).</param>
    /// <param name="expect">Words it should expect, so they come out spelled right (the bot's name, game terms).</param>
    public async Task<string> TranscribeAsync(float[] samples, string expect, CancellationToken cancellationToken)
    {
        if (Problem is not null)
        {
            return "";
        }

        await _one.WaitAsync(cancellationToken);
        try
        {
            if (_factory is null)
            {
                try
                {
                    _factory = WhisperFactory.FromPath(_modelPath);
                }
                catch (Exception ex)
                {
                    // Reported like a missing model, through Problem, rather than failing every sentence.
                    _logger.LogWarning(ex, "Couldn't load the speech-to-text model at {Path}.", _modelPath);
                    _loadProblem = $"The speech-to-text model at {_options.ModelPath} wouldn't load: run " +
                                   "voice/setup.ps1 again (it replaces a damaged download).";
                    return "";
                }
            }
            await using var processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt(expect)
                .Build();

            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
            {
                // Speech-to-text makes words up out of noise (a breath, a keyboard, a game in the background): often
                // the words it was told to expect ("Hey ModBot." - a chime for nothing), or "Thank you.". It also
                // says how likely the stretch was to have no speech in it at all, so those are dropped.
                if (segment.NoSpeechProbability >= NoSpeechCutoff)
                {
                    _logger.LogDebug("Dropped a stretch of voice that was likely not speech ({Probability:P0}).",
                        segment.NoSpeechProbability);
                    continue;
                }

                parts.Add(segment.Text.Trim());
            }

            return string.Join(' ', parts).Trim();
        }
        finally
        {
            _one.Release();
        }
    }

    private string? FindProblem()
    {
        if (!_options.Enabled)
        {
            return "Voice commands are turned off in the bot's settings (Voice:Enabled).";
        }

        // On Windows they sit next to the exe (voice/setup.ps1 puts them there). Elsewhere they're usually installed
        // system-wide under other names, so only loading libdave below is checked.
        var missing = OperatingSystem.IsWindows()
            ? NativeLibraries.Where(name => !File.Exists(Path.Combine(AppContext.BaseDirectory, $"{name}.dll"))).ToList()
            : [];
        if (missing.Count > 0)
        {
            return $"The voice libraries aren't installed ({string.Join(", ", missing)}): run voice/setup.ps1 " +
                   "(see voice/README.md), then rebuild.";
        }

        try
        {
            if (!Dave.CheckAvailability())
            {
                return "Discord's voice encryption library (libdave) didn't load: run voice/setup.ps1 again.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't load libdave.");
            return "Discord's voice encryption library (libdave) didn't load: run voice/setup.ps1 again.";
        }

        return File.Exists(_modelPath)
            ? null
            : $"The speech-to-text model isn't at {_options.ModelPath}: run voice/setup.ps1 (see voice/README.md).";
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _one.Dispose();
    }
}
