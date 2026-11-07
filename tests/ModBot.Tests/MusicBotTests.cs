using Discord;
using Discord.WebSocket;
using ModBot.Hosting;

namespace ModBot.Tests;

/// <summary>Music on the main bot, or on its own account (Bot:MusicToken).</summary>
public class MusicBotTests
{
    [Fact]
    public void Without_a_music_account_the_main_bot_plays()
    {
        using var main = new DiscordSocketClient();
        var music = new MusicBot(main, separate: null);

        Assert.False(music.IsSeparate);
        Assert.Same(main, music.Client);
    }

    [Fact]
    public void With_one_it_plays_from_that_account()
    {
        using var main = new DiscordSocketClient();
        using var separate = new DiscordSocketClient(MusicBot.ClientConfig);
        var music = new MusicBot(main, separate);

        Assert.True(music.IsSeparate);
        Assert.Same(separate, music.Client);
    }

    [Fact]
    public void The_music_account_asks_for_only_what_voice_needs()
    {
        // No privileged intents (members, presences, message content): it only joins voice.
        Assert.Equal(GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates, MusicBot.ClientConfig.GatewayIntents);

        using var separate = new DiscordSocketClient(MusicBot.ClientConfig);
        using var main = new DiscordSocketClient();
        var link = new MusicBot(main, separate).InviteLink;
        var permissions = new GuildPermissions(ulong.Parse(link[(link.LastIndexOf('=') + 1)..]));
        Assert.True(permissions is { ViewChannel: true, Connect: true, Speak: true });
        Assert.Equal(3, permissions.ToList().Count);
    }
}
