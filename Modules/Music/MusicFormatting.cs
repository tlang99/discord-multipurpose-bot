using System.Text.RegularExpressions;
using Discord;
using Lavalink4NET.Players;
using Lavalink4NET.Players.Queued;
using Lavalink4NET.Tracks;
using ModBot.Common;
using ModBot.Models;

namespace ModBot.Modules.Music;

public static partial class MusicFormatting
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

    /// <summary>A wait as people say it: "about 12 min", or "under a minute".</summary>
    public static string RoughWait(TimeSpan wait)
        => wait < TimeSpan.FromMinutes(1) ? "under a minute" : $"about {(int)Math.Ceiling(wait.TotalMinutes)} min";

    public static string Length(LavalinkTrack track) => Length(track.IsLiveStream, track.Duration);

    public static string SongLength(QueuedSong song) => Length(song.IsLiveStream, song.Duration);
    public static string MenuTrackLink(LavalinkTrack track)
        => Link(SafeTitle(CleanTitle(track.Title, track.Author)), track.Uri?.ToString(), noPreview: true);

    /// <summary>A saved song for the menu, tidied like <see cref="MenuTrackLink"/>.</summary>
    public static string MenuSongLink(QueuedSong song) => Link(SafeTitle(CleanTitle(song.Title)), song.Uri, noPreview: true);

    /// <summary>
    /// A YouTube title as a music player shows it: without "(Official Video)", "[Lyrics]" and the like, and without
    /// the artist's name in front when it's shown on its own line anyway. Anything else in brackets (a remix, a
    /// "feat.", a live version) is part of the song and stays. Returns the title as it was if nothing's left.
    /// </summary>
    public static string CleanTitle(string? title, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title ?? string.Empty;
        }

        var cleaned = Clutter().Replace(title, "").Trim();
        if (ArtistPrefixLength(cleaned, Artist(author)) is > 0 and var end &&
            Separator().Match(cleaned, end) is { Success: true } separator &&
            end + separator.Length < cleaned.Length)
        {
            cleaned = cleaned[(end + separator.Length)..];
        }

        cleaned = cleaned.Trim(' ', '-', '|', '–', '—');
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    /// <summary>How much of the start of <paramref name="title"/> is <paramref name="artist"/>'s name, ignoring case
    /// and spaces (channels like "TheWeekndVEVO" run the words together); 0 if it doesn't start with it.</summary>
    private static int ArtistPrefixLength(string title, string artist)
    {
        var name = artist.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray();
        if (name.Length == 0)
        {
            return 0;
        }

        var matched = 0;
        for (var i = 0; i < title.Length; i++)
        {
            if (char.IsWhiteSpace(title[i]))
            {
                continue;
            }

            if (char.ToLowerInvariant(title[i]) != name[matched])
            {
                return 0;
            }

            if (++matched == name.Length)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>A search for the same song somewhere else: its title without the clutter, with the artist in front
    /// when the title doesn't already name one ("Artist - Song"). A re-upload's channel isn't the artist, so a title
    /// that names one is searched as it is.</summary>
    public static string SongSearch(string? title, string? author)
    {
        var cleaned = Clutter().Replace(title ?? "", "").Trim();
        var named = NamesArtist().IsMatch(cleaned);
        var artist = Artist(author);
        return (named || artist.Length == 0 ? cleaned : $"{artist} {cleaned}").Trim();
    }

    /// <summary>The uploader as an artist's name: YouTube Music's "Name - Topic" channels and "NameVEVO" without the
    /// tag. Not escaped.</summary>
    public static string Artist(string? author)
    {
        var name = (author ?? string.Empty).Trim();
        if (name.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^" - Topic".Length];
        }
        else if (name.Length > 4 && name.EndsWith("VEVO", StringComparison.Ordinal))
        {
            name = name[..^4];
        }

        return name.Trim();
    }

    /// <summary>A bracketed bit of a title that's about the upload, not the song: made only of upload words ("(Official
    /// Music Video)", "[Lyrics]", "(HD Audio)"). A bracket with anything else in it stays: "(Official Remix)",
    /// "(Acoustic Version)", "(Video Game Soundtrack)" are about the song.</summary>
    [GeneratedRegex(@"\s*[\(\[]\s*(?:(?:official|music|lyrics?|lyric|audio|video|visuali[sz]er|hd|hq|4k|mv|clip)[\s/&,-]*)+[\)\]]",
        RegexOptions.IgnoreCase)]
    private static partial Regex Clutter();

    /// <summary>The " - " (or similar) between an artist's name and the song in "Artist - Song".</summary>
    [GeneratedRegex(@"\G\s*[-–—|:]\s+")]
    private static partial Regex Separator();

    /// <summary>A title in the "Artist - Song" form: a dash (or bar) with spaces either side, anywhere in it.</summary>
    [GeneratedRegex(@"\s[-–—|]\s")]
    private static partial Regex NamesArtist();

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
            .AddField("By", TextTools.Escape(track.Author ?? "Unknown"), inline: true)
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
