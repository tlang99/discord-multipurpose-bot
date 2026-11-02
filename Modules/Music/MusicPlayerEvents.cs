using Lavalink4NET.Players;
using Microsoft.Extensions.Logging;

namespace ModBot.Modules.Music;

/// <summary>
/// What the music players do, for code that needs to follow along (music sessions update their menu when a song
/// starts, and end when the bot leaves voice). One shared instance, handed to every player through its options, so
/// the players don't depend on the session code.
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

    /// <summary>The player is gone: /stop, the idle timeout, or the bot was disconnected. Gets the server ID.</summary>
    public Func<ulong, Task>? PlayerClosed { get; set; }

    internal Task RaiseTrackStartedAsync(MusicPlayer player, ITrackQueueItem item)
        => RunAsync(TrackStarted is { } handler ? () => handler(player, item) : null, player.GuildId);

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
            // A listener failing (e.g. couldn't edit a menu) mustn't break playback.
            _logger.LogWarning(ex, "A music player listener failed in guild {GuildId}.", guildId);
        }
    }
}
