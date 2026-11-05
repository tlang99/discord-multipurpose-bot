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
}
