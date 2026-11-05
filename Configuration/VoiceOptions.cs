namespace ModBot.Configuration;

/// <summary>
/// Voice commands: the bot listens in a voice channel for its name ("hey ModBot, skip this song") and does what was
/// asked, like a chat message. Speech is turned into text on this machine (Whisper); no audio is saved. Needs the
/// native libraries and model from voice/README.md. Each server also has to turn it on (/settings feature).
/// </summary>
public class VoiceOptions
{
    public const string SectionName = "Voice";

    /// <summary>False turns voice commands off for every server, whatever they chose.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Whisper speech-to-text model, relative to the bot's folder. voice/setup.ps1 downloads it.</summary>
    public string ModelPath { get; set; } = "voice/models/ggml-base.en.bin";

    /// <summary>How long someone has to pause before what they said counts as finished, in milliseconds.</summary>
    public int PauseMilliseconds { get; set; } = 800;

    /// <summary>Longest stretch of speech turned into text at once, in seconds: past this it's cut and checked.</summary>
    public int MaxSpeechSeconds { get; set; } = 15;

    /// <summary>Stop listening after this many minutes, even if people are still in the channel (0 = never).</summary>
    public int MaxListenMinutes { get; set; } = 180;
}
