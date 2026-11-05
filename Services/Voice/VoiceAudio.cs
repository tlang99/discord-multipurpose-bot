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

    /// <summary>The loudest the chime gets, as a share of full volume: soft, so it doesn't make anyone jump.</summary>
    public const double ChimeVolume = 0.2;

    /// <summary>
    /// The "I heard my name" chime, in Discord's format (48 kHz stereo 16-bit): two short rising notes, a quarter of a
    /// second in all. Made here rather than shipped as a sound file, so there's nothing extra to download. Each note
    /// fades in and out, which keeps it from clicking.
    /// </summary>
    public static byte[] Chime()
    {
        const int rate = 48_000;
        (double Frequency, double Seconds)[] notes = [(1318.5, 0.09), (1760.0, 0.15)]; // E6, then A6
        var samples = new List<short>();
        foreach (var (frequency, seconds) in notes)
        {
            var count = (int)(rate * seconds);
            var attack = rate * 0.005;
            for (var i = 0; i < count; i++)
            {
                var fade = Math.Min(1, i / attack) * Math.Pow(1 - (double)i / count, 2);
                samples.Add((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * fade * ChimeVolume * short.MaxValue));
            }
        }

        var pcm = new byte[samples.Count * 4];
        for (var i = 0; i < samples.Count; i++)
        {
            // The same sample in both channels.
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), samples[i]);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), samples[i]);
        }

        return pcm;
    }
}
