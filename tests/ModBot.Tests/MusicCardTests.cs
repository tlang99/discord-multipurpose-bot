using ModBot.Modules.Music;

namespace ModBot.Tests;

/// <summary>The music menu's card: tidied song titles and the accent color taken from the cover.</summary>
public sealed class MusicCardTests
{
    [Theory]
    [InlineData("Mr. Brightside (Official Music Video)", "Mr. Brightside")]
    [InlineData("Blinding Lights [Lyrics]", "Blinding Lights")]
    [InlineData("Song (HD Audio)", "Song")]
    [InlineData("Song (Official Video) [4K]", "Song")]
    public void Upload_words_in_brackets_are_tidied_away(string title, string cleaned)
        => Assert.Equal(cleaned, MusicFormatting.CleanTitle(title));

    [Theory]
    // A re-upload names the artist in its title; its channel isn't the artist, so it's left out.
    [InlineData("Drake - Fucking Fans (Audio)", "SomeLyricsChannel", "Drake - Fucking Fans")]
    // A title without the artist gets them from the channel, without YouTube Music's "- Topic" or "VEVO".
    [InlineData("Juicy (Official Video)", "The Notorious B.I.G. - Topic", "The Notorious B.I.G. Juicy")]
    [InlineData("Mr. Brightside", "TheKillersVEVO", "TheKillers Mr. Brightside")]
    [InlineData("Stay Fly", "", "Stay Fly")]
    public void Another_copy_is_searched_for_by_artist_and_song(string title, string author, string search)
        => Assert.Equal(search, MusicFormatting.SongSearch(title, author));

    [Theory]
    [InlineData("Song (Official Remix)")]
    [InlineData("Song (Official Acoustic Version)")]
    [InlineData("Theme (Video Game Soundtrack)")]
    [InlineData("Song (Live at Wembley)")]
    public void Brackets_about_the_song_stay(string title)
        => Assert.Equal(title, MusicFormatting.CleanTitle(title));

    [Fact]
    public void A_cover_mostly_one_colour_gives_that_colour()
    {
        var red = Enumerable.Repeat(new AlbumArt.Rgb(200, 30, 30), 1600).ToArray();

        var accent = AlbumArt.PickAccent(red);

        Assert.True(accent.R > accent.G * 2 && accent.R > accent.B * 2);
    }

    [Fact]
    public void A_grey_cover_gives_a_grey_that_reads_on_discord()
    {
        var grey = Enumerable.Repeat(new AlbumArt.Rgb(20, 20, 20), 1600).ToArray();

        var accent = AlbumArt.PickAccent(grey);

        Assert.Equal(accent.R, accent.G);
        Assert.InRange(accent.R, 140, 192); // never near-black, which vanishes on a dark theme
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", "https://i.ytimg.com/vi/dQw4w9WgXcQ/mqdefault.jpg")]
    [InlineData("http://example.com/cover.jpg", null)] // only https covers are shown
    public void Covers_are_shown_from_https_only(string url, string? shown)
        => Assert.Equal(shown, AlbumArt.DisplayUrl(new Uri(url)));
}
