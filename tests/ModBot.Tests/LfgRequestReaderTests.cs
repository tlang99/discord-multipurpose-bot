using ModBot.Models;
using ModBot.Services.Chat;

namespace ModBot.Tests;

public class LfgRequestReaderTests
{
    [Theory]
    [InlineData("make an lfg, need a more chill squad", 4)] // "a more" isn't "one more"
    [InlineData("make an lfg, need one more", 2)]
    [InlineData("make an lfg for ranked, need 3 more, mic required", 4)]
    [InlineData("anyone want to run hardpoint? make a post for 4", 4)]
    [InlineData("make an lfg", 4)]
    [InlineData("post an lfg for a duo, gold+ ranked, pc only", 2)]
    [InlineData("make an lfg for hardcore domination with 2 more people, mics optional", 3)]
    [InlineData("make a 3 stack lfg for tdm, no mic needed", 3)]
    [InlineData("squad of five for search and destroy", 5)]
    public void Reads_squad_size(string message, int expectedPlayers)
        => Assert.Equal(expectedPlayers, LfgRequestReader.Read(message).Players);

    [Fact]
    public void Reads_rank_mic_and_platform()
    {
        var (_, details) = LfgRequestReader.Read("post an lfg for a duo, gold+ ranked, pc only, mic required");

        Assert.Equal(LfgPlaylist.RankedPlay, details.Playlist);
        Assert.Equal(LfgRank.Gold, details.MinRank);
        Assert.True(details.MicRequired);
        Assert.Equal(LfgPlatform.PcOnly, details.Platform);
    }

    [Fact]
    public void A_rank_means_ranked_play()
    {
        var (_, details) = LfgRequestReader.Read("make an lfg, diamond+");

        Assert.Equal(LfgPlaylist.RankedPlay, details.Playlist);
        Assert.Equal(LfgRank.Diamond, details.MinRank);
    }

    [Fact]
    public void No_rank_outside_ranked_play()
    {
        var (_, details) = LfgRequestReader.Read("make an lfg for hardpoint");

        Assert.Equal(LfgPlaylist.Hardpoint, details.Playlist);
        Assert.Null(details.MinRank);
    }

    [Theory]
    [InlineData("make an lfg, mic not required", false)]
    [InlineData("make an lfg, no mic", false)]
    [InlineData("make an lfg, need a mic", true)]
    [InlineData("make an lfg", null)]
    public void Reads_mic(string message, bool? expected) => Assert.Equal(expected, LfgRequestReader.Read(message).Details.MicRequired);
}
