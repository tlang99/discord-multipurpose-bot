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

    /// <summary>Read the answer to a spoken request aloud instead of posting it (where it can't be said, it's posted).
    /// Text-to-speech runs on this machine (Piper); needs the Speak permission, and Piper from voice/setup.ps1.</summary>
    public bool SpokenReplies { get; set; } = true;

    /// <summary>Piper's program, relative to the bot's folder. voice/setup.ps1 downloads it.</summary>
    public string PiperPath { get; set; } = "voice/piper/piper.exe";

    /// <summary>The voice Piper speaks with (a .onnx file, with its .onnx.json next to it). voice/setup.ps1
    /// downloads one; others are at https://huggingface.co/rhasspy/piper-voices.</summary>
    public string SpeakingVoicePath { get; set; } = "voice/models/en_US-kusal-medium.onnx";

    /// <summary>The most of an answer read aloud, in characters (whole sentences, 50-2000), so the bot doesn't talk
    /// over people for long. Anything past it isn't said.</summary>
    public int MaxSpokenCharacters { get; set; } = 300;

    /// <summary>
    /// After an answer read aloud, anyone in the voice channel can say the next thing without the bot's name, until
    /// nobody's said anything to it for this many seconds (0-60; 0 turns conversations off), or someone says
    /// "thanks" or "that's all". A closing chime says it's over.
    /// </summary>
    public int ConversationSeconds { get; set; } = 10;

    /// <summary>
    /// Write each voice request (as speech-to-text heard it) to the log, for working out why one wasn't understood:
    /// what was said after the bot's name, and in a conversation, the replies. Off by default: the log
    /// otherwise never holds what anyone said. While it's on, the line posted on joining voice says so.
    /// </summary>
    public bool LogRequests { get; set; }
}
