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
    public static byte[] Chime()
    {
        // A marimba's bar rings at its note plus faint overtones at 4x and 10x, which die away faster than the note.
        (double Ratio, double Level)[] overtones = [(4.0, 0.25), (10.0, 0.05)];
        var samples = Note(784.0, 0.16, decay: 0.06, overtones).Concat(Note(1046.5, 0.32, decay: 0.10, overtones))
            .ToArray();
        var peak = samples.Max(Math.Abs);

        // Padded with silence to whole 20 ms frames: the audio encoder holds back a part-frame until more comes, which
        // would cut off the end of the chime.
        var pcm = new byte[(samples.Length * 4 + FrameBytes - 1) / FrameBytes * FrameBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = (short)(samples[i] / peak * ChimeVolume * short.MaxValue);
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
