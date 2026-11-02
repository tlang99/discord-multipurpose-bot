using Lavalink4NET.Players;
using Lavalink4NET.Protocol.Payloads.Events;
using Microsoft.Extensions.Logging;

namespace ModBot.Modules.Music;

/// <summary>
/// What the music players do, for the standing queue to follow along: it plays the next turn when a song ends and
/// keeps its menu up to date. One shared instance, handed to every player through its options, so the players don't
/// depend on the queue code.
/// </summary>
public sealed class MusicPlayerEvents
{
    private readonly ILogger<MusicPlayerEvents> _logger;

    public MusicPlayerEvents(ILogger<MusicPlayerEvents> logger)
    {
        _logger = logger;
    }

    /// <summary>A song started playing.</summary>
    public Func<MusicPlayer, ITrackQueueItem, Task>? TrackStarted { get; set; }

    /// <summary>A song finished or failed to load (not skipped or stopped): time for the next one.</summary>
    public Func<MusicPlayer, ITrackQueueItem, TrackEndReason, Task>? TrackEnded { get; set; }

    /// <summary>The player is gone: /stop, the idle timeout, or the bot was disconnected. Gets the server ID.</summary>
    public Func<ulong, Task>? PlayerClosed { get; set; }

    /// <summary>Whether to post "Now playing" for each song in a server. Not where the music menu already shows it:
    /// the extra posts would just push the menu up.</summary>
    public Func<ulong, bool>? AnnounceSongs { get; set; }

    internal bool ShouldAnnounce(ulong guildId) => AnnounceSongs?.Invoke(guildId) ?? true;

    internal Task RaiseTrackStartedAsync(MusicPlayer player, ITrackQueueItem item)
        => RunAsync(TrackStarted is { } handler ? () => handler(player, item) : null, player.GuildId);

    internal Task RaiseTrackEndedAsync(MusicPlayer player, ITrackQueueItem item, TrackEndReason reason)
        => RunAsync(TrackEnded is { } handler ? () => handler(player, item, reason) : null, player.GuildId);

    internal Task RaisePlayerClosedAsync(ulong guildId)
        => RunAsync(PlayerClosed is { } handler ? () => handler(guildId) : null, guildId);

    private async Task RunAsync(Func<Task>? handler, ulong guildId)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            // A listener failing (e.g. couldn't edit the menu) mustn't break playback.
            _logger.LogWarning(ex, "A music player listener failed in guild {GuildId}.", guildId);
        }
    }
}
