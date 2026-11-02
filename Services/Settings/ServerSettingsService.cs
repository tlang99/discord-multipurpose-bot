using Discord;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Storage;

namespace ModBot.Services.Settings;

/// <summary>
/// Where a feature works in one server. Either anywhere, or one channel: <see cref="Channel"/>, which is null when
/// the server doesn't have it (yet, or any more), with <see cref="Name"/> saying what's expected.
/// </summary>
public sealed record FeatureChannel(bool Anywhere, ITextChannel? Channel, string Name)
{
    public static readonly FeatureChannel AnyChannel = new(true, null, "");

    /// <summary>Whether the feature can be used in <paramref name="channel"/>.</summary>
    public bool Allows(IChannel channel) => Anywhere || Channel?.Id == channel.Id;

    /// <summary>Where the feature's posts go: its channel, or <paramref name="asked"/> when it works anywhere. Null
    /// when its channel is missing.</summary>
    public ITextChannel? Or(ITextChannel asked) => Anywhere ? asked : Channel;

    /// <summary>A clickable mention of the channel (or its name if it's missing), or null when it works anywhere.</summary>
    public string? Where => Anywhere ? null : Channel?.Mention ?? (Name.Length > 0 ? $"#{Name}" : "(deleted channel)");

    /// <summary>"<paramref name="whatGoes"/> in #name, but this server doesn't have that channel yet", or, for a
    /// chosen channel that was deleted, how to pick another.</summary>
    /// <param name="whatGoes">e.g. "Music goes" or "LFG posts go".</param>
    public string Missing(string whatGoes)
        => Name.Length > 0
            ? ConfiguredChannels.Missing(whatGoes, Name)
            : $"{whatGoes} in a channel that's since been deleted. A server admin can pick another with " +
              "`/settings channel`.";

    /// <summary>"Music commands go in #music - run them there", or <see cref="Missing"/>.</summary>
    public string GoThere(string whatGoes, string command)
        => Channel is { } channel ? $"{whatGoes} in {channel.Mention} - run {command} there." : Missing(whatGoes);
}

/// <summary>The role LFG posts ping in one server: <see cref="Role"/> if it exists, else <see cref="Name"/> is what
/// /lfg-setup creates. <see cref="Off"/> when pings are turned off.</summary>
public sealed record LfgPingRole(IRole? Role, string Name, bool Off);

/// <summary>
/// Reads each server's own settings (<see cref="ServerSettings"/>, set with /settings) and turns them into the
/// channels, role and switches every feature uses, falling back to the bot's defaults from appsettings.json for
/// anything a server hasn't set. Every feature asks here rather than reading the options itself, so a server's choice
/// applies everywhere the same way: slash commands, buttons, the chatbot and /help.
/// </summary>
public class ServerSettingsService
{
    private readonly IServerSettingsStore _store;
    private readonly MusicOptions _music;
    private readonly LfgOptions _lfg;

    public ServerSettingsService(IServerSettingsStore store, IOptions<MusicOptions> music, IOptions<LfgOptions> lfg)
    {
        _store = store;
        _music = music.Value;
        _lfg = lfg.Value;
    }

    public Task<ServerSettings> GetAsync(ulong guildId) => _store.GetAsync(guildId);

    public Task UpdateAsync(ulong guildId, Action<ServerSettings> update) => _store.UpdateAsync(guildId, update);

    public async Task<bool> IsEnabledAsync(ulong guildId, ServerFeature feature)
        => (await _store.GetAsync(guildId)).IsEnabled(feature);

    public async Task<FeatureChannel> MusicChannelAsync(IGuild guild)
        => await ResolveAsync(guild, (await _store.GetAsync(guild.Id)).Music, _music.ChannelName);

    public async Task<FeatureChannel> LfgChannelAsync(IGuild guild)
        => await ResolveAsync(guild, (await _store.GetAsync(guild.Id)).Lfg, _lfg.ChannelName);

    /// <summary>The bot's default channel for a feature, from appsettings.json ("" = anywhere), for /settings show.</summary>
    public string DefaultChannelName(ServerFeature feature)
        => ConfiguredChannels.ChannelName(feature == ServerFeature.Music ? _music.ChannelName : _lfg.ChannelName);

    public string DefaultPingRoleName => ConfiguredChannels.RoleName(_lfg.PingRoleName);

    public async Task<LfgPingRole> LfgPingRoleAsync(IGuild guild)
    {
        var settings = await _store.GetAsync(guild.Id);
        if (settings.LfgPingsOff)
        {
            return new(null, "", Off: true);
        }

        if (settings.LfgPingRoleId is { } id)
        {
            var chosen = guild.GetRole(id);
            return new(chosen, chosen?.Name ?? "", Off: false);
        }

        // The default is found by name, and is off when no name is configured.
        var name = DefaultPingRoleName;
        return name.Length == 0
            ? new(null, "", Off: true)
            : new(guild.Roles.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), name,
                Off: false);
    }

    /// <summary>"Music", "LFG", "Chat actions"...</summary>
    public static string NameOf(ServerFeature feature) => feature switch
    {
        ServerFeature.Chat => "The chatbot",
        ServerFeature.ChatActions => "Asking the chatbot to do things",
        ServerFeature.Music => "Music",
        ServerFeature.Lfg => "LFG",
        ServerFeature.Polls => "Polls",
        ServerFeature.Reminders => "Reminders",
        _ => feature.ToString(),
    };

    /// <summary>What members are told when they use a feature their server turned off.</summary>
    public static string TurnedOff(ServerFeature feature)
        => $"{NameOf(feature)} is turned off on this server. A server admin can turn it on with `/settings feature`.";

    private static async Task<FeatureChannel> ResolveAsync(IGuild guild, ChannelChoice choice, string defaultName)
    {
        if (choice.Anywhere)
        {
            return FeatureChannel.AnyChannel;
        }

        if (choice.ChannelId is { } id)
        {
            var chosen = await guild.GetTextChannelAsync(id, CacheMode.CacheOnly);
            return new(false, chosen, chosen?.Name ?? "");
        }

        var name = ConfiguredChannels.ChannelName(defaultName);
        if (name.Length == 0)
        {
            return FeatureChannel.AnyChannel;
        }

        var channels = await guild.GetTextChannelsAsync(CacheMode.CacheOnly);
        return new(false, channels.FirstOrDefault(c => ConfiguredChannels.IsNamed(c, name)), name);
    }
}
