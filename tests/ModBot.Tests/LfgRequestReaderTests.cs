using ModBot.Models;
using ModBot.Modules.Lfg;
using ModBot.Services.Chat;
using ModBot.Services.Lfg;

namespace ModBot.Tests;

/// <summary>Games as a server would type them into /settings lfg-game. Call of Duty is the README's example, and
/// what the bot used to have built in, so its old reader tests still run against it.</summary>
internal static class LfgGames
{
    public static readonly LfgGameProfile CallOfDuty = Parse(
        """
        Ranked Play* : ranked, comp, competitive
        Hardcore : hc
        Search & Destroy : search and destroy, s&d, snd, snd's
        Hardpoint : hardpoints, hp
        Team Deathmatch : tdm
        Kill Confirmed : kc
        Domination : dom
        """,
        """
        Bronze
        Silver
        Gold
        Platinum : plat
        Diamond : diamonds
        Crimson
        Iridescent : iri
        Top 250 : top250
        """);

    /// <summary>Ranks that go with any mode: no playlist is marked ranked.</summary>
    public static readonly LfgGameProfile RocketLeague = Parse(
        """
        Doubles : 2s, 2v2
        Standard : 3s, 3v3
        """,
        """
        Gold
        Platinum : plat
        Diamond
        Champion : champ
        Grand Champion : gc
        """);

    private static LfgGameProfile Parse(string playlists, string ranks)
        => LfgGameProfileText.Parse(playlists, ranks).Profile ?? throw new InvalidOperationException("bad fixture");
}

public class LfgRequestReaderTests
{
    private static (int Players, LfgDetails Details) Cod(string message)
        => LfgRequestReader.Read(message, LfgGames.CallOfDuty);

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
        => Assert.Equal(expectedPlayers, Cod(message).Players);

    [Fact]
    public void Reads_rank_mic_and_platform()
    {
        var (_, details) = Cod("post an lfg for a duo, gold+ ranked, pc only, mic required");

        Assert.Equal("Ranked Play", details.Playlist);
        Assert.Equal("Gold", details.MinRank);
        Assert.True(details.MicRequired);
        Assert.Equal(LfgPlatform.PcOnly, details.Platform);
    }

    [Fact]
    public void A_rank_means_the_ranked_playlist()
    {
        var (_, details) = Cod("make an lfg, diamond+");

        Assert.Equal("Ranked Play", details.Playlist);
        Assert.Equal("Diamond", details.MinRank);
    }

    [Fact]
    public void No_rank_outside_the_ranked_playlist()
    {
        var (_, details) = Cod("make an lfg for hardpoint");

        Assert.Equal("Hardpoint", details.Playlist);
        Assert.Null(details.MinRank);
    }

    [Theory]
    [InlineData("make an lfg for ranked hardpoint", "Ranked Play")] // ranked playlists are checked first
    [InlineData("make an lfg for hardcore domination", "Hardcore")] // then the server's list order
    [InlineData("make an lfg for s&d", "Search & Destroy")]
    public void Picks_the_playlist_the_server_lists_first(string message, string playlist)
        => Assert.Equal(playlist, Cod(message).Details.Playlist);

    [Fact]
    public void The_top_rank_is_marked()
    {
        var (_, details) = Cod("make an lfg for top 250");

        Assert.Equal("Top 250", details.MinRank);
        Assert.True(details.MinRankIsTop);
        Assert.Equal("🏆 Top 250", LfgMessages.Tags(details));
        Assert.Equal("🏆 Gold+", LfgMessages.Tags(Cod("make an lfg, gold+").Details));
    }

    [Theory]
    [InlineData("make an lfg, mic not required", false)]
    [InlineData("make an lfg, no mic", false)]
    [InlineData("make an lfg, need a mic", true)]
    [InlineData("make an lfg", null)]
    public void Reads_mic(string message, bool? expected) => Assert.Equal(expected, Cod(message).Details.MicRequired);

    [Fact]
    public void A_blank_server_reads_no_playlist_or_rank_but_everything_else()
    {
        var (players, details) = LfgRequestReader.Read("make an lfg for ranked hardpoint, gold+, need 2 more, pc only",
            LfgGameProfile.Empty);

        Assert.Equal(3, players);
        Assert.Null(details.Playlist);
        Assert.Null(details.MinRank);
        Assert.Equal(LfgPlatform.PcOnly, details.Platform);
    }

    [Fact]
    public void Another_game_reads_its_own_words()
    {
        var (_, details) = LfgRequestReader.Read("make an lfg for 2s, champ+", LfgGames.RocketLeague);

        Assert.Equal("Doubles", details.Playlist);
        Assert.Equal("Champion", details.MinRank); // no ranked playlists: a rank goes with any mode
    }
}

public class LfgGameProfileTests
{
    [Fact]
    public void Parses_names_aliases_and_the_ranked_mark()
    {
        var game = LfgGames.CallOfDuty;

        Assert.Equal(7, game.Playlists.Count);
        Assert.True(game.Playlists[0].Ranked);
        Assert.Equal(["ranked", "comp", "competitive"], game.Playlists[0].Aliases);
        Assert.Equal(8, game.Ranks.Count);
        Assert.Equal("Top 250", game.Ranks[^1].Name);
    }

    [Fact]
    public void Round_trips_through_the_pop_up_text()
    {
        var (playlists, ranks) = LfgGameProfileText.Format(LfgGames.CallOfDuty);
        var again = LfgGameProfileText.Parse(playlists, ranks).Profile!;

        Assert.Equal(LfgGameProfileText.Format(again), (playlists, ranks));
        Assert.StartsWith("Ranked Play* : ranked, comp, competitive", playlists);
    }

    [Fact]
    public void Both_boxes_empty_is_blank()
        => Assert.True(LfgGameProfileText.Parse("", "  \n ").Profile!.IsEmpty);

    [Theory]
    [InlineData("Hardpoint\nhardpoint", "", "already used by **the Hardpoint playlist**")] // same name twice
    [InlineData("Hardpoint : hp\nHot Pursuit : hp", "", "already used by **the Hardpoint playlist**")] // alias clash
    [InlineData("Diamond Mode", "Gold\nDiamond mode", "already used by **the Diamond Mode playlist**")] // across boxes
    [InlineData("", "Gold*", "only marks a ranked **playlist**")]
    [InlineData(" : hp", "", "line 1")]
    public void Reports_the_line_that_is_wrong(string playlists, string ranks, string expected)
    {
        var (profile, problem) = LfgGameProfileText.Parse(playlists, ranks);

        Assert.Null(profile);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Refuses_more_than_discord_can_suggest()
    {
        var lines = string.Join("\n", Enumerable.Range(1, LfgGameProfileText.MaxEntries + 1).Select(i => $"Mode {i}"));

        Assert.Contains("at most 25", LfgGameProfileText.Parse(lines, "").Problem);
    }

    [Fact]
    public void Finds_a_value_by_name_or_alias()
    {
        Assert.Equal("Search & Destroy", LfgGames.CallOfDuty.FindPlaylist("SND")?.Name);
        Assert.Equal("Platinum", LfgGames.CallOfDuty.FindRank(" plat ")?.Name);
        Assert.Null(LfgGames.CallOfDuty.FindPlaylist("ranked hardpoint"));
    }

    [Fact]
    public void A_rank_needs_a_ranked_playlist_when_the_game_has_one()
    {
        var cod = LfgGames.CallOfDuty;
        var gold = cod.FindRank("gold");

        Assert.Equal("Ranked Play", cod.Combine(null, gold).Playlist?.Name);
        Assert.Contains("only applies to **Ranked Play**", cod.Combine(cod.FindPlaylist("hp"), gold).Problem);
        Assert.Null(LfgGames.RocketLeague.Combine(LfgGames.RocketLeague.FindPlaylist("2s"),
            LfgGames.RocketLeague.FindRank("gc")).Problem);
    }
}

public class LfgGameProfileLengthTests
{
    [Fact]
    public void Refuses_a_list_that_would_not_fit_back_in_the_pop_up()
    {
        // 25 lines, each with 10 other names typed without spaces: fits the 4000-character box as typed, but not once
        // tidied (" : " and ", "), which is what the pop-up would be pre-filled with next time.
        var lines = Enumerable.Range(1, 25).Select(i =>
            $"M{i:00}:" + string.Join(",", Enumerable.Range(0, 10).Select(j => $"{i:00}{j}".PadRight(14, 'x'))));
        var typed = string.Join("\n", lines);

        var (profile, problem) = LfgGameProfileText.Parse(typed, "");

        Assert.True(typed.Length <= LfgGameProfileText.MaxBoxLength);
        Assert.Null(profile);
        Assert.Contains("too long to fit back", problem);
    }

    [Fact]
    public void A_saved_list_always_fits_back_in_the_pop_up()
    {
        var (playlists, ranks) = LfgGameProfileText.Format(LfgGames.CallOfDuty);

        Assert.True(playlists.Length <= LfgGameProfileText.MaxBoxLength);
        Assert.True(ranks.Length <= LfgGameProfileText.MaxBoxLength);
    }
}

public class LfgGameRobustnessTests
{
    [Fact]
    public void A_hand_edited_file_with_nulls_loads_as_empty_values()
    {
        var game = System.Text.Json.JsonSerializer.Deserialize<LfgGameProfile>(
            """{"Playlists":[null,{"Name":null,"Aliases":null}],"Ranks":null}""")!;

        Assert.Single(game.Playlists);
        Assert.Empty(game.Ranks);
        Assert.Null(game.FindPlaylist("hp")); // and nothing throws
        Assert.False(game.IsEmpty);
    }

    [Fact]
    public void The_pop_up_text_stays_within_discords_limits()
    {
        // Over these, Discord refuses to open the pop-up at all.
        Assert.True(ModBot.Modules.Settings.LfgGameModal.PlaylistsPlaceholder.Length <= 100);
        Assert.True(ModBot.Modules.Settings.LfgGameModal.RanksPlaceholder.Length <= 100);
        Assert.True(ModBot.Modules.Settings.LfgGameModal.PlaylistsLabel.Length <= 45);
        Assert.True(ModBot.Modules.Settings.LfgGameModal.RanksLabel.Length <= 45);
        Assert.True(ModBot.Modules.Settings.LfgGameModal.ModalTitle.Length <= 45);
    }
}
