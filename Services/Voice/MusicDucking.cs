namespace ModBot.Services.Voice;

/// <summary>
/// Turning the music down while the bot reads an answer over it, and back up after. Answers can overlap (one waits on
/// another), so they're counted: the first one turns it down, the last one back up. It's put back to what it was,
/// unless someone changed the volume meanwhile: then that's what it stays at. Safe to use from several threads.
/// </summary>
public sealed class MusicDucking
{
    private readonly Lock _lock = new();
    private int _talking;
    private float? _before;
    private float? _loweredTo;

    /// <summary>An answer starts: true if it's the only one, so it's the one to turn the music down.</summary>
    public bool Begin() => Interlocked.Increment(ref _talking) == 1;

    /// <summary>An answer's done: true if it was the last one, so it's the one to turn the music back up.</summary>
    public bool End() => Interlocked.Decrement(ref _talking) == 0;

    /// <summary>How loud music at <paramref name="volume"/> plays while the bot talks:
    /// <paramref name="percent"/> of it (Voice:MusicDuckPercent, 0-100; 100 leaves it as it is).</summary>
    public static float Lowered(float volume, int percent) => volume * Math.Clamp(percent, 0, 100) / 100f;

    /// <summary>The music was turned down from <paramref name="before"/>; <paramref name="to"/> is what the player
    /// reports it's at now (it may round).</summary>
    public void TurnedDown(float before, float to)
    {
        lock (_lock)
        {
            (_before, _loweredTo) = (before, to);
        }
    }

    /// <summary>What to set the music back to, now that it's at <paramref name="now"/>: what it was, or null to leave
    /// it (it wasn't turned down, or someone has changed it since). Answers once per turn-down.</summary>
    public float? ToRestore(float now)
    {
        lock (_lock)
        {
            var (before, loweredTo) = (_before, _loweredTo);
            (_before, _loweredTo) = (null, null);
            return before is { } was && loweredTo is { } lowered && Math.Abs(now - lowered) < 0.01f ? was : null;
        }
    }
}
