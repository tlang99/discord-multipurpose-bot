using Lavalink4NET.Players;
using Lavalink4NET.Tracks;

namespace ModBot.Modules.Music;

/// <summary>A queued song that remembers who picked it, so a music session can take turns fairly and the picker can
/// skip their own song. Songs queued outside a session use Lavalink's plain item.</summary>
public sealed record SessionTrackItem(LavalinkTrack LavalinkTrack, ulong PickerId, string PickerName) : ITrackQueueItem
{
    /// <summary>Tells apart two picks of the same song.</summary>
    public Guid EntryId { get; } = Guid.NewGuid();

    public TrackReference Reference => new(LavalinkTrack);

    public LavalinkTrack Track => LavalinkTrack;

    public string Identifier => LavalinkTrack.Identifier;

    public T? As<T>() where T : class, ITrackQueueItem => this as T;
}
