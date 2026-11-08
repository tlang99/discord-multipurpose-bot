using Discord;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Modules.Lfg;
using ModBot.Services.Settings;
using ModBot.Services.Storage;

namespace ModBot.Tests;

public sealed class ServerSettingsServiceTests : IDisposable
{
    private const ulong GuildId = 1;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));
    private readonly ServerSettingsService _settings;

    public ServerSettingsServiceTests()
    {
        var store = new JsonServerSettingsStore(Path.Combine(_directory, "server-settings.json"));
        _settings = new ServerSettingsService(store, Options.Create(new LfgOptions { PingRoleName = "LFG" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Everything_is_on_for_a_new_server_but_the_opt_in_features()
    {
        var server = await _settings.GetAsync(GuildId);

        Assert.All(Enum.GetValues<ServerFeature>(), f => Assert.Equal(!ServerSettings.IsOptIn(f), server.IsEnabled(f)));
    }

    [Fact]
    public async Task Dj_mode_is_off_until_a_server_turns_it_on()
    {
        var server = await _settings.GetAsync(GuildId);

        Assert.True(ServerSettings.IsOptIn(ServerFeature.MusicDj)); // it plays songs nobody asked for
        Assert.False(server.IsEnabled(ServerFeature.MusicDj));
    }

    [Fact]
    public async Task Turning_a_feature_off_only_affects_that_server()
    {
        await _settings.UpdateAsync(GuildId, s => s.DisabledFeatures.Add(nameof(ServerFeature.Polls)));

        Assert.False(await _settings.IsEnabledAsync(GuildId, ServerFeature.Polls));
        Assert.True(await _settings.IsEnabledAsync(GuildId, ServerFeature.Music));
        Assert.True(await _settings.IsEnabledAsync(2, ServerFeature.Polls));
    }

    [Fact]
    public async Task Works_in_any_channel_until_the_server_picks_one()
    {
        // No default channel name: even a server with a #music channel isn't limited until it picks one.
        var asked = Channel(5, "general");

        var music = await _settings.MusicChannelAsync(Guild(channels: [Channel(10, "music"), asked]));
        var lfg = await _settings.LfgChannelAsync(Guild());

        Assert.True(music.Anywhere);
        Assert.True(lfg.Anywhere);
        Assert.True(music.Allows(asked));
        Assert.Same(asked, music.Or(asked));
    }

    [Fact]
    public async Task A_picked_channel_limits_it_to_that_channel()
    {
        var chosen = Channel(20, "tunes");
        await _settings.UpdateAsync(GuildId, s => s.Lfg.ChannelId = chosen.Id);

        var where = await _settings.LfgChannelAsync(Guild(byId: chosen));

        Assert.True(where.Allows(chosen));
        Assert.False(where.Allows(Channel(5, "general")));
        Assert.Same(chosen, where.Or(Channel(5, "general")));
    }

    [Fact]
    public async Task A_chosen_channel_is_found_by_id_whatever_its_name()
    {
        var chosen = Channel(20, "tunes-renamed");
        await _settings.UpdateAsync(GuildId, s => s.Music.ChannelId = chosen.Id);

        var where = await _settings.MusicChannelAsync(Guild(byId: chosen));

        Assert.Same(chosen, where.Channel);
    }

    [Fact]
    public async Task A_deleted_chosen_channel_points_to_settings()
    {
        await _settings.UpdateAsync(GuildId, s => s.Music.ChannelId = 99);

        var where = await _settings.MusicChannelAsync(Guild(byId: null));

        Assert.Null(where.Channel);
        Assert.Contains("/settings channel", where.Missing("Music goes"));
    }

    [Fact]
    public async Task Clearing_the_pick_allows_any_channel_again()
    {
        await _settings.UpdateAsync(GuildId, s => s.Music.ChannelId = 20);
        await _settings.UpdateAsync(GuildId, s => s.Music.ChannelId = null);

        Assert.True((await _settings.MusicChannelAsync(Guild())).Anywhere);
    }

    [Fact]
    public void Auto_mod_starts_off_for_a_new_server() => Assert.False(new GuildSettings().AutoModEnabled);

    [Fact]
    public async Task Ping_role_defaults_to_the_configured_name()
    {
        var lfg = Role(30, "lfg");

        var ping = await _settings.LfgPingRoleAsync(Guild(roles: [lfg]));

        Assert.Same(lfg, ping.Role);
        Assert.False(ping.Off);
    }

    [Fact]
    public async Task Ping_role_can_be_chosen_or_turned_off()
    {
        var chosen = Role(31, "squad-pings");
        await _settings.UpdateAsync(GuildId, s => s.LfgPingRoleId = chosen.Id);
        Assert.Same(chosen, (await _settings.LfgPingRoleAsync(Guild(roleById: chosen))).Role);

        await _settings.UpdateAsync(GuildId, s => s.LfgPingsOff = true);
        Assert.True((await _settings.LfgPingRoleAsync(Guild(roleById: chosen))).Off);
    }

    private static IGuild Guild(ITextChannel[]? channels = null, ITextChannel? byId = null, IRole[]? roles = null,
        IRole? roleById = null)
        => DiscordFake.Create<IGuild>(new()
        {
            ["get_Id"] = GuildId,
            ["GetTextChannelsAsync"] = Task.FromResult<IReadOnlyCollection<ITextChannel>>(channels ?? []),
            ["GetTextChannelAsync"] = Task.FromResult(byId),
            ["get_Roles"] = (IReadOnlyCollection<IRole>)(roles ?? []),
            ["GetRole"] = roleById,
        });

    private static ITextChannel Channel(ulong id, string name)
        => DiscordFake.Create<ITextChannel>(new() { ["get_Id"] = id, ["get_Name"] = name, ["get_Mention"] = $"<#{id}>" });

    private static IRole Role(ulong id, string name)
        => DiscordFake.Create<IRole>(new() { ["get_Id"] = id, ["get_Name"] = name });
}

public class LfgPingRoleSafetyTests
{
    [Fact]
    public async Task Refuses_everyone()
    {
        var everyone = Role(1, GuildPermissions.None);
        Assert.Equal("is @everyone", await LfgPoster.WhyUnsafeToSelfAssignAsync(Guild(everyone), everyone));
    }

    [Fact]
    public async Task Refuses_a_role_with_extra_permissions()
    {
        var everyone = Role(1, GuildPermissions.None);
        var mods = Role(2, new GuildPermissions(kickMembers: true));

        Assert.Contains("beyond @everyone's", await LfgPoster.WhyUnsafeToSelfAssignAsync(Guild(everyone), mods));
    }

    [Fact]
    public async Task Allows_a_plain_role()
    {
        var everyone = Role(1, new GuildPermissions(viewChannel: true));
        var plain = Role(2, new GuildPermissions(viewChannel: true));

        Assert.Null(await LfgPoster.WhyUnsafeToSelfAssignAsync(Guild(everyone), plain));
    }

    [Fact]
    public async Task Refuses_a_role_a_channel_grants_permissions_to()
    {
        // Checked through the server's channel list, so any server object works (the check used to be skipped for
        // anything but a live gateway server).
        var everyone = Role(1, GuildPermissions.None);
        var plain = Role(2, GuildPermissions.None);
        var secret = DiscordFake.Create<IGuildChannel>(new()
        {
            ["get_Name"] = "secret",
            ["GetPermissionOverwrite"] = (OverwritePermissions?)new OverwritePermissions(viewChannel: PermValue.Allow),
        });

        Assert.Equal("grants permissions in #secret",
            await LfgPoster.WhyUnsafeToSelfAssignAsync(Guild(everyone, secret), plain));
    }

    private static IGuild Guild(IRole everyone, params IGuildChannel[] channels)
        => DiscordFake.Create<IGuild>(new()
        {
            ["get_EveryoneRole"] = everyone,
            ["GetChannelsAsync"] = Task.FromResult<IReadOnlyCollection<IGuildChannel>>(channels),
        });

    private static IRole Role(ulong id, GuildPermissions permissions)
        => DiscordFake.Create<IRole>(new()
        {
            ["get_Id"] = id, ["get_IsManaged"] = false, ["get_Permissions"] = permissions,
        });
}

public class SettingsRequestReaderTests
{
    [Theory]
    [InlineData("disable polls", ServerFeature.Polls, false)]
    [InlineData("enable the chatbot", ServerFeature.Chat, true)]
    [InlineData("can you disable music for this server please", ServerFeature.Music, false)]
    [InlineData("enable music", ServerFeature.Music, true)]
    [InlineData("disable the  chat bot", ServerFeature.Chat, false)]
    [InlineData("turn off lfg for this server", ServerFeature.Lfg, false)]
    [InlineData("turn reminders on here", ServerFeature.Reminders, true)]
    [InlineData("disable chat actions", ServerFeature.ChatActions, false)]
    [InlineData("switch on polls on this server", ServerFeature.Polls, true)]
    public void Reads_feature_switches(string message, ServerFeature feature, bool on)
        => Assert.Equal(new FeatureSwitchRequest(feature, on), SettingsRequestReader.Read(message));

    [Theory]
    [InlineData("turn off the music")] // stop the song, not the feature
    [InlineData("turn the music off")]
    [InlineData("how do I disable polls?")]
    [InlineData("don't disable polls")]
    [InlineData("disable polls and then make one")]
    [InlineData("disable the profanity filter")] // that's /automod
    [InlineData("disable music")] // could mean "stop the song"; switching it off for everyone needs "for this server"
    [InlineData("can you disable music please")]
    public void Ignores_everything_else(string message) => Assert.Null(SettingsRequestReader.Read(message));
}

public class BotPostFilterTests
{
    [Fact]
    public void The_bots_own_words_are_always_checked_auto_mod_on_or_off()
        => Assert.True(Services.Moderation.TextFilter.AppliesToBotPost(null));

    [Fact]
    public void A_moderators_own_words_are_not()
    {
        var mod = DiscordFake.Create<IGuildUser>(new() { ["get_GuildPermissions"] = new GuildPermissions(manageMessages: true) });
        Assert.False(Services.Moderation.TextFilter.AppliesToBotPost(mod));
    }
}
