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

    /// <summary>Stop listening after this many minutes, even if people are still in the channel (0 = never). Only when
    /// someone started it: when the bot joined by itself (voice auto-join), it stays until everyone's left.</summary>
    public int MaxListenMinutes { get; set; } = 180;

    /// <summary>Play a short chime when the bot hears its name, so people know it's listening. Needs the Speak
    /// permission in the voice channel; without it, the bot just doesn't chime.</summary>
    public bool Chime { get; set; } = true;

    /// <summary>Read the answer to a spoken request aloud (it's still posted in chat too). Text-to-speech runs on this
    /// machine (Piper); needs the Speak permission, and Piper from voice/setup.ps1.</summary>
    public bool SpokenReplies { get; set; } = true;

    /// <summary>Piper's program, relative to the bot's folder. voice/setup.ps1 downloads it.</summary>
    public string PiperPath { get; set; } = "voice/piper/piper.exe";

    /// <summary>The voice Piper speaks with (a .onnx file, with its .onnx.json next to it). voice/setup.ps1
    /// downloads one; others are at https://huggingface.co/rhasspy/piper-voices.</summary>
    public string SpeakingVoicePath { get; set; } = "voice/models/en_US-kusal-medium.onnx";

    /// <summary>The most of an answer read aloud, in characters (whole sentences): the rest is only in chat, so the
    /// bot doesn't talk over people for long.</summary>
    public int MaxSpokenCharacters { get; set; } = 300;
}
