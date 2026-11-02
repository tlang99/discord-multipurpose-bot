using Discord;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Protocol.Payloads.Events;
using Lavalink4NET.Rest.Entities.Tracks;
using Lavalink4NET.Tracks;
using Microsoft.Extensions.Logging;
using ModBot.Models;

namespace ModBot.Modules.Music;

public record MusicPlayerOptions : QueuedLavalinkPlayerOptions
{
    /// <summary>Where "Now playing" and playback errors get posted: the music channel.</summary>
    public ITextChannel? TextChannel { get; init; }

    /// <summary>Told when songs start and end and when the player goes away, so the standing queue can play the next
    /// turn and keep its menu up to date.</summary>
    public MusicPlayerEvents? Events { get; init; }
}

/// <summary>A song from the standing queue, as the player plays it: keeps the saved song (and so who picked it).</summary>
public sealed record QueueTrackItem(LavalinkTrack LavalinkTrack, QueuedSong Song) : ITrackQueueItem
{
    public TrackReference Reference => new(LavalinkTrack);

    public LavalinkTrack Track => LavalinkTrack;

    public string Identifier => LavalinkTrack.Identifier;

    public T? As<T>() where T : class, ITrackQueueItem => this as T;
}

/// <summary>
/// The Lavalink player. It plays one song at a time: the standing queue (<see cref="MusicQueueService"/>) holds what's
/// waiting and hands over the next turn when a song ends, so Lavalink's own queue stays empty. Announces each song as
/// it starts, so people in the voice channel can see what's playing.
/// </summary>
public sealed class MusicPlayer : QueuedLavalinkPlayer
{
    private readonly ITextChannel? _textChannel;
    private readonly MusicPlayerEvents? _events;
    private readonly ILogger _logger;
    private string? _lastAnnounced;

    public MusicPlayer(IPlayerProperties<MusicPlayer, MusicPlayerOptions> properties)
        : base(properties)
    {
        _textChannel = properties.Options.Value.TextChannel;
        _events = properties.Options.Value.Events;
        _logger = properties.Logger;
    }

    protected override async ValueTask NotifyTrackStartedAsync(ITrackQueueItem track, CancellationToken cancellationToken = default)
    {
        await base.NotifyTrackStartedAsync(track, cancellationToken);

        if (_events is not null)
        {
            await _events.RaiseTrackStartedAsync(this, track);
        }

        var identifier = track.Track?.Identifier;
        if (identifier is null || _textChannel is null || _events?.ShouldAnnounce(GuildId) == false)
        {
            return;
        }

        // On a looped song, announcing every repeat would just spam the channel.
        var repeat = RepeatMode == TrackRepeatMode.Track && identifier == _lastAnnounced;
        _lastAnnounced = identifier;
        if (repeat)
        {
            return;
        }

        await SendAsync(MusicFormatting.NowPlayingEmbed(track.Track!, this,
            track is QueueTrackItem item ? $"Now playing - {Format.Sanitize(item.Song.PickerName)}'s pick" : "Now playing"));
    }

    protected override async ValueTask NotifyTrackEndedAsync(ITrackQueueItem queueItem, TrackEndReason endReason,
        CancellationToken cancellationToken = default)
    {
        // A looped song that can't load would otherwise be retried forever.
        if (endReason == TrackEndReason.LoadFailed && RepeatMode == TrackRepeatMode.Track)
        {
            RepeatMode = TrackRepeatMode.None;
        }

        await base.NotifyTrackEndedAsync(queueItem, endReason, cancellationToken);

        // Finished or failed: time for the next turn - unless the song is looping, which the base player replays.
        // Stopped/replaced means a skip or /stop, which already decided what happens next.
        var looped = endReason == TrackEndReason.Finished && RepeatMode == TrackRepeatMode.Track;
        if (_events is not null && endReason is (TrackEndReason.Finished or TrackEndReason.LoadFailed) && !looped)
        {
            await _events.RaiseTrackEndedAsync(this, queueItem, endReason);
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore();
        if (_events is not null)
        {
            await _events.RaisePlayerClosedAsync(GuildId);
        }
    }

    protected override async ValueTask NotifyTrackExceptionAsync(ITrackQueueItem track, TrackException exception,
        CancellationToken cancellationToken = default)
    {
        await base.NotifyTrackExceptionAsync(track, exception, cancellationToken);

        _logger.LogWarning("Couldn't play '{Title}' ({Uri}) in guild {GuildId}: {Message} ({Cause})",
            track.Track?.Title, track.Track?.Uri, GuildId, exception.Message, exception.Cause);

        if (_textChannel is not null)
        {
            // The real reason is in the log (one line per YouTube client tried); for the channel, the likely causes.
            await SendAsync(new EmbedBuilder()
                .WithColor(Color.Red)
                .WithDescription($"⚠️ YouTube wouldn't let me play {MusicFormatting.TrackLink(track.Track)}. It may be " +
                                 "age-restricted or blocked in this region, or YouTube is blocking the bot right now. " +
                                 "Moving on to the next turn.")
                .Build());
        }
    }

    private async Task SendAsync(Embed embed)
    {
        try
        {
            await _textChannel!.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            // Losing an announcement (e.g. the bot lost access to the channel) shouldn't interrupt playback.
            _logger.LogWarning(ex, "Couldn't post a music announcement to channel {ChannelId}.", _textChannel!.Id);
        }
    }
}
