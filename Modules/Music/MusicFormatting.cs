using Discord;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Tracks;

namespace ModBot.Modules.Music;

public static class MusicFormatting
{
    private const int ProgressBarLength = 18;

    /// <summary>Song titles come from uploaders, so escape markdown and keep them short.</summary>
    public static string Title(LavalinkTrack? track)
    {
        var title = track?.Title ?? "Unknown track";
        if (title.Length > 80)
        {
            title = title[..77] + "...";
        }

        // Format.Sanitize doesn't escape square brackets, and titles go inside [title](url) links: a title like
        // "x](https://evil) [y" would otherwise break out of the link text and add a link of its own.
        return Format.Sanitize(title).Replace("[", "\\[").Replace("]", "\\]");
    }

    public static string TrackLink(LavalinkTrack? track)
        => track?.Uri is { } uri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? $"[{Title(track)}]({uri})"
            : $"**{Title(track)}**";

    public static string Duration(TimeSpan duration)
        => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    public static string Length(LavalinkTrack track)
        => track.IsLiveStream ? "live" : Duration(track.Duration);

    public static string RepeatLabel(TrackRepeatMode mode) => mode switch
    {
        TrackRepeatMode.Track => "song",
        TrackRepeatMode.Queue => "queue",
        _ => "off",
    };

    public static Embed NowPlayingEmbed(LavalinkTrack track, IQueuedLavalinkPlayer player, string heading)
    {
        var builder = new EmbedBuilder()
            .WithColor(new Color(0x1DB954))
            .WithAuthor(heading)
            .WithDescription(TrackLink(track))
            .AddField("By", Format.Sanitize(track.Author ?? "Unknown"), inline: true)
            .AddField("Length", Length(track), inline: true);

        if (player.Queue.Count > 0)
        {
            builder.AddField("Up next", $"{Title(player.Queue[0].Track)} (+{player.Queue.Count - 1} more)", inline: false);
        }

        if (player.RepeatMode != TrackRepeatMode.None)
        {
            builder.WithFooter($"Loop: {RepeatLabel(player.RepeatMode)}");
        }

        if (track.ArtworkUri is { Scheme: "https" } artwork)
        {
            builder.WithThumbnailUrl(artwork.ToString());
        }

        return builder.Build();
    }

    public static string ProgressBar(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        var filled = (int)Math.Clamp(position / duration * ProgressBarLength, 0, ProgressBarLength);
        return $"`{new string('▬', filled)}🔘{new string('▬', ProgressBarLength - filled)}` " +
               $"{Duration(position)} / {Duration(duration)}";
    }
}
