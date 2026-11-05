using Discord;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Tracks;
using ModBot.Common;
using ModBot.Models;

namespace ModBot.Modules.Music;

public static class MusicFormatting
{
    private const int ProgressBarLength = 18;

    public static string Title(LavalinkTrack? track) => SafeTitle(track?.Title);

    /// <summary>A song from Lavalink as a clickable title, for embeds.</summary>
    public static string TrackLink(LavalinkTrack? track)
        => Link(Title(track), track?.Uri?.ToString(), noPreview: false);

    /// <summary>A saved song as a clickable title, wrapped so Discord doesn't unfurl the link in a
    /// message.</summary>
    public static string SongLink(QueuedSong song) => Link(SafeTitle(song.Title), song.Uri, noPreview: true);

    public static string Duration(TimeSpan duration)
        => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    public static string Length(LavalinkTrack track) => Length(track.IsLiveStream, track.Duration);

    public static string SongLength(QueuedSong song) => Length(song.IsLiveStream, song.Duration);

    /// <summary>Song titles come from uploaders, so escape markdown and keep them short.</summary>
    private static string SafeTitle(string? title)
    {
        title ??= "Unknown track";
        if (title.Length > 80)
        {
            title = title[..77] + "...";
        }

        // Titles go inside [title](url) links, so the brackets are escaped too (see TextTools.Escape).
        return TextTools.Escape(title);
    }

    /// <summary>A link when there's a web address to go to, else the title in bold.</summary>
    private static string Link(string safeTitle, string? uri, bool noPreview)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return $"**{safeTitle}**";
        }

        // AbsoluteUri escapes spaces and the like, but leaves brackets: a ")" or ">" in the address would end the
        // markdown link early and let the rest of it show (or link) as something else.
        var address = parsed.AbsoluteUri.Replace("(", "%28").Replace(")", "%29").Replace("<", "%3C").Replace(">", "%3E");
        return noPreview ? $"[{safeTitle}](<{address}>)" : $"[{safeTitle}]({address})";
    }

    private static string Length(bool isLiveStream, TimeSpan duration) => isLiveStream ? "live" : Duration(duration);

    public static string RepeatLabel(TrackRepeatMode mode) => mode switch
    {
        TrackRepeatMode.Track => "song",
        TrackRepeatMode.Queue => "queue",
        _ => "off",
    };

    public static Embed NowPlayingEmbed(LavalinkTrack track, IQueuedLavalinkPlayer player, string heading)
    {
        var builder = new EmbedBuilder()
            .WithColor(Colors.Music)
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
