namespace ModBot.Services.Voice;

/// <summary>The audio formats involved: what Discord gives the bot, and what speech-to-text (Whisper) wants.</summary>
public static class VoiceAudio
{
    /// <summary>Discord's decoded voice: 48 kHz, stereo, 16-bit samples. 4 bytes per sample pair.</summary>
    public const int DiscordBytesPerSecond = 48_000 * 2 * 2;

    /// <summary>Whisper's input: 16 kHz mono.</summary>
    public const int WhisperSampleRate = 16_000;

    /// <summary>
    /// Discord's audio (48 kHz stereo 16-bit) as Whisper's (16 kHz mono floats from -1 to 1). Each output sample is the
    /// average of three input samples across both channels, which also filters out what 16 kHz can't hold.
    /// </summary>
    public static float[] ToWhisperSamples(ReadOnlySpan<byte> pcm)
    {
        var output = new float[pcm.Length / 4 / 3];
        for (var i = 0; i < output.Length; i++)
        {
            var sum = 0;
            for (var j = 0; j < 3; j++)
            {
                var at = (i * 3 + j) * 4;
                sum += BitConverter.ToInt16(pcm[at..]) + BitConverter.ToInt16(pcm[(at + 2)..]);
            }

            output[i] = sum / (6f * 32768f);
        }

        return output;
    }

    /// <summary>How long <paramref name="bytes"/> of Discord audio lasts.</summary>
    public static TimeSpan DurationOf(long bytes) => TimeSpan.FromSeconds((double)bytes / DiscordBytesPerSecond);

    /// <summary>One 20 ms frame of Discord audio, the unit it's encoded and sent in.</summary>
    public const int FrameBytes = DiscordBytesPerSecond / 50;

    /// <summary>The loudest the chime gets, as a share of full volume: soft, so it doesn't make anyone jump.</summary>
    public const double ChimeVolume = 0.2;

    /// <summary>
    /// The "I heard my name" chime, in Discord's format (48 kHz stereo 16-bit): two soft marimba-like notes rising (G5,
    /// then C6), about half a second in all. Made here rather than shipped as a sound file, so there's nothing extra to
    /// download. Each note strikes quickly and dies away naturally, with a short fade at the end to keep it from
    /// clicking.
    /// </summary>
    public static byte[] Chime() => Notes(784.0, 1046.5);

    /// <summary>The "I'm done listening" chime, for when a conversation ends: <see cref="Chime"/> the other way round,
    /// falling (C6, then G5).</summary>
    public static byte[] ExitChime() => Notes(1046.5, 784.0);

    /// <summary>The "done" chime, for a request whose result you can hear (the music started, stopped or skipped):
    /// three notes, low, high, low (C6, E6, C6), a step above <see cref="Chime"/> so it's told apart from "I heard my
    /// name" and the closing chime.</summary>
    public static byte[] DoneChime() => Notes(1046.5, 1318.5, 1046.5);

    /// <summary>Quick marimba-like notes in a row, the last one left to ring out.</summary>
    private static byte[] Notes(params double[] frequencies)
    {
        // A marimba's bar rings at its note plus faint overtones at 4x and 10x, which die away faster than the note.
        (double Ratio, double Level)[] overtones = [(4.0, 0.25), (10.0, 0.05)];
        var samples = frequencies.SelectMany((frequency, i) => i < frequencies.Length - 1
                ? Note(frequency, 0.16, decay: 0.06, overtones)
                : Note(frequency, 0.32, decay: 0.10, overtones))
            .ToArray();
        var peak = samples.Max(Math.Abs);
        return ToDiscord(samples, ChimeVolume / peak);
    }

    /// <summary>The share of full volume spoken replies are played at: a little under, so they don't clip or blare.</summary>
    public const double SpeechVolume = 0.8;

    /// <summary>
    /// Speech from text-to-speech (16-bit mono at <paramref name="sampleRate"/>, Piper's raw output) in Discord's
    /// format, resampled to 48 kHz by straight-line interpolation between samples, which is plenty for a voice.
    /// </summary>
    public static byte[] FromSpeech(ReadOnlySpan<byte> mono16, int sampleRate)
    {
        var count = mono16.Length / 2;
        if (count == 0 || sampleRate <= 0)
        {
            return [];
        }

        var output = new double[(int)((long)count * 48_000 / sampleRate)];
        var step = (double)sampleRate / 48_000;
        for (var i = 0; i < output.Length; i++)
        {
            var at = i * step;
            var before = (int)at;
            var after = Math.Min(before + 1, count - 1);
            var weight = at - before;
            output[i] = BitConverter.ToInt16(mono16[(before * 2)..]) * (1 - weight) +
                        BitConverter.ToInt16(mono16[(after * 2)..]) * weight;
        }

        return ToDiscord(output, SpeechVolume / short.MaxValue);
    }

    /// <summary>48 kHz mono <paramref name="samples"/>, times <paramref name="gain"/> to make full volume 1, as
    /// Discord's 48 kHz stereo 16-bit, padded with silence to whole 20 ms frames: the audio encoder holds back a
    /// part-frame until more comes, which would cut off the end of the sound.</summary>
    private static byte[] ToDiscord(IReadOnlyList<double> samples, double gain)
    {
        var pcm = new byte[(samples.Count * 4 + FrameBytes - 1) / FrameBytes * FrameBytes];
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = (short)Math.Clamp(samples[i] * gain * short.MaxValue, short.MinValue, short.MaxValue);
            // The same sample in both channels.
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), sample);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), sample);
        }

        return pcm;
    }

    /// <summary>One struck note, <paramref name="seconds"/> long: <paramref name="decay"/> is how quickly it dies away
    /// (the time to fall to about a third).</summary>
    private static IEnumerable<double> Note(double frequency, double seconds, double decay,
        (double Ratio, double Level)[] overtones)
    {
        const int rate = 48_000;
        var count = (int)(rate * seconds);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            var strike = Math.Min(1, t / 0.002); // 2 ms to full, so it's crisp without a click
            var end = Math.Min(1, (count - i) / (rate * 0.01)); // the last 10 ms fade to nothing
            var sound = Math.Sin(2 * Math.PI * frequency * t);
            foreach (var (ratio, level) in overtones)
            {
                sound += level * Math.Sin(2 * Math.PI * frequency * ratio * t) * Math.Exp(-t / (decay / 2));
            }

            yield return sound * strike * Math.Exp(-t / decay) * end;
        }
    }
}
