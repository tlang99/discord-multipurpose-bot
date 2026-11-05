using ModBot.Models;
using ModBot.Modules.Lfg;
using ModBot.Services.Lfg;

namespace ModBot.Tests;

/// <summary>The game's name from /settings lfg-game, and where LFG posts show it.</summary>
public class LfgGameNameTests
{
    private static LfgGameProfile Parse(string? name, string playlists = "Hardpoint : hp", string ranks = "")
        => LfgGameProfileText.Parse(playlists, ranks, name).Profile ?? throw new InvalidOperationException("bad input");

    [Fact]
    public void Name_is_saved_trimmed_on_one_line()
        => Assert.Equal("Call of Duty: MW4", Parse("  Call of Duty:\r\n MW4 ").Name);

    [Fact]
    public void Name_can_be_left_empty()
        => Assert.Equal("", Parse(null).Name);

    [Fact]
    public void A_name_alone_is_a_game()
        => Assert.False(Parse("Valorant", playlists: "").IsEmpty);

    [Fact]
    public void Too_long_a_name_is_refused()
    {
        var (profile, problem) = LfgGameProfileText.Parse("", "", new string('x', LfgGameProfileText.MaxGameNameLength + 1));

        Assert.Null(profile);
        Assert.Contains("Game name", problem);
    }

    [Fact]
    public void Name_doesnt_follow_the_chat_name_rules()
    {
        // A playlist can't be called "3" or "lfg" (chat would misread them), but the name is never matched in chat.
        Assert.Equal("Apex Legends 2", Parse("Apex Legends 2").Name);
    }

    [Fact]
    public void Posts_copy_the_name_when_made()
    {
        var game = Parse("Call of Duty: MW4");
        var details = LfgDetails.From(game, game.Combine(game.Playlists[0], null), mic: null, LfgPlatform.Crossplay);

        Assert.Equal("Call of Duty: MW4", details.Game);
        Assert.Null(LfgDetails.From(Parse(null), new LfgChoice(null, null), null, LfgPlatform.Crossplay).Game);
    }

    [Theory]
    [InlineData("Call of Duty: MW4", "Hardpoint", "Call of Duty: MW4 · Hardpoint")]
    [InlineData("Call of Duty: MW4", null, "Call of Duty: MW4")]
    [InlineData(null, "Hardpoint", "Hardpoint")]
    [InlineData(null, null, "LFG")]
    public void Card_heading_shows_the_game_and_playlist(string? game, string? playlist, string expected)
        => Assert.Equal(ShownAs(expected), LfgMessages.Heading(new LfgDetails(Playlist: playlist, Game: game)));

    [Theory]
    [InlineData("Call of Duty: MW4", "Hardpoint", "Hardpoint squad in Call of Duty: MW4")]
    [InlineData("Call of Duty: MW4", null, "squad in Call of Duty: MW4")]
    [InlineData(null, "Hardpoint", "Hardpoint squad")]
    [InlineData(null, null, "squad")]
    public void Pings_name_the_game(string? game, string? playlist, string expected)
        => Assert.Equal(ShownAs(expected), LfgMessages.Squad(new LfgDetails(Playlist: playlist, Game: game)));

    /// <summary>The text as Discord gets it: Format.Sanitize escapes ":" (Discord shows ":" as ":").</summary>
    private static string ShownAs(string text) => text.Replace(":", @"\:");

    [Fact]
    public void Game_name_markdown_is_shown_as_typed()
        => Assert.Equal(@"\*\*Big\*\* \[game\]", LfgMessages.Heading(new LfgDetails(Game: "**Big** [game]")));
}

/// <summary>What members type into an LFG post ends up in an embed, where [text](url) would be a link.</summary>
public class LfgNoteEscapingTests
{
    [Fact]
    public void A_link_in_the_note_isnt_clickable()
    {
        var post = new LfgPost { HostId = 1, Size = 4, Members = [1], Note = "[free nitro](https://evil.example)" };

        var description = LfgMessages.OpenEmbed(post).Description;

        Assert.Contains(@"\[free nitro\]", description);
        Assert.DoesNotContain("[free nitro](", description);
    }
}
