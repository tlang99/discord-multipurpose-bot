using Discord;
using Discord.WebSocket;
using ModBot.Services.Music;

namespace ModBot.Tests;

/// <summary>Music on the main bot, or on its own account (Bot:MusicToken).</summary>
public class MusicBotTests
{
    [Fact]
    public void Without_a_music_account_the_main_bot_plays()
    {
        using var main = new DiscordSocketClient();
        using var music = new MusicBot(main, separate: null);

        Assert.False(music.IsSeparate);
        Assert.Same(main, music.Client);
    }

    [Fact]
    public void With_one_it_plays_from_that_account()
    {
        using var main = new DiscordSocketClient();
        using var music = new MusicBot(main, new DiscordSocketClient(MusicBot.NewClientConfig()));

        Assert.True(music.IsSeparate);
        Assert.NotSame(main, music.Client);
    }

    [Fact]
    public void The_music_account_asks_for_only_what_voice_needs()
    {
        // No privileged intents (members, presences, message content): it only joins voice.
        Assert.Equal(GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates, MusicBot.NewClientConfig().GatewayIntents);
        Assert.True(MusicBot.InvitePermissions is { ViewChannel: true, Connect: true, Speak: true });
        Assert.Equal(3, MusicBot.InvitePermissions.ToList().Count);
    }

    [Fact]
    public void Theres_no_invite_link_until_it_has_signed_in()
    {
        // The link needs the account's ID: a refused token mustn't hand out "client_id=".
        using var main = new DiscordSocketClient();
        using var music = new MusicBot(main, new DiscordSocketClient(MusicBot.NewClientConfig()));

        Assert.False(music.IsSignedIn);
        Assert.Null(music.InviteLink);
    }

    [Theory]
    [InlineData("music-token", "main-token", true)]
    [InlineData("main-token", "main-token", false)] // the same account twice would knock itself off
    [InlineData(" main-token ", "main-token", false)]
    [InlineData("", "main-token", false)]
    [InlineData(null, "main-token", false)]
    public void Only_a_different_token_makes_a_music_account(string? music, string main, bool separate)
        => Assert.Equal(separate, MusicBot.IsSeparateToken(music, main));
}
