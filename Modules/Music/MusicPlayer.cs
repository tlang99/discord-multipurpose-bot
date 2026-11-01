using Discord;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Rest.Entities.Tracks;
using Microsoft.Extensions.Logging;

namespace ModBot.Modules.Music;

public record MusicPlayerOptions : QueuedLavalinkPlayerOptions
{
    /// <summary>Where "Now playing" and playback errors get posted - the channel /play was first used in.</summary>
    public ITextChannel? TextChannel { get; init; }
}

/// <summary>
/// A queued Lavalink player that announces each song as it starts, so people in the voice channel can see what's
/// playing without running /nowplaying.
/// </summary>
public sealed class MusicPlayer : QueuedLavalinkPlayer
{
    private readonly ITextChannel? _textChannel;
    private readonly ILogger _logger;

    // The song /play started directly is already announced in that command's reply, so skip it once here.
    private string? _skipAnnouncementFor;
    private string? _lastAnnounced;

    public MusicPlayer(IPlayerProperties<MusicPlayer, MusicPlayerOptions> properties)
        : base(properties)
    {
        _textChannel = properties.Options.Value.TextChannel;
        _logger = properties.Logger;
    }

    public void SuppressNextAnnouncement(string trackIdentifier) => _skipAnnouncementFor = trackIdentifier;

    /// <summary>Undoes <see cref="SuppressNextAnnouncement"/> when the song never started.</summary>
    public void ClearSuppressedAnnouncement() => _skipAnnouncementFor = null;

    protected override async ValueTask NotifyTrackStartedAsync(ITrackQueueItem track, CancellationToken cancellationToken = default)
    {
        await base.NotifyTrackStartedAsync(track, cancellationToken);

        var identifier = track.Track?.Identifier;
        if (identifier is null || _textChannel is null)
        {
            return;
        }

        var suppressed = identifier == _skipAnnouncementFor;
        _skipAnnouncementFor = null;

        // On a looped song, announcing every repeat would just spam the channel.
        var repeat = RepeatMode == TrackRepeatMode.Track && identifier == _lastAnnounced;
        _lastAnnounced = identifier;
        if (suppressed || repeat)
        {
            return;
        }

        await SendAsync(MusicFormatting.NowPlayingEmbed(track.Track!, this, "Now playing"));
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
            var next = Queue.Count > 0 ? "Skipping to the next song." : "Nothing else is queued.";
            await SendAsync(new EmbedBuilder()
                .WithColor(Color.Red)
                .WithDescription($"⚠️ YouTube wouldn't let me play {MusicFormatting.TrackLink(track.Track)}. It may be " +
                                 $"age-restricted or blocked in this region, or YouTube is blocking the bot right now. {next}")
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
