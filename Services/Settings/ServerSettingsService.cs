using Discord;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Storage;

namespace ModBot.Services.Settings;

/// <summary>
/// Where a feature works in one server: anywhere, or the one channel the server picked. <see cref="Channel"/> is null
/// when the picked channel has since been deleted.
/// </summary>
public sealed record FeatureChannel(bool Anywhere, ITextChannel? Channel)
{
    public static readonly FeatureChannel AnyChannel = new(true, null);

    /// <summary>Whether the feature can be used in <paramref name="channel"/>.</summary>
    public bool Allows(IChannel channel) => Anywhere || Channel?.Id == channel.Id;

    /// <summary>Where the feature's posts go: its channel, or <paramref name="asked"/> when it works anywhere. Null
    /// when its channel was deleted.</summary>
    public ITextChannel? Or(ITextChannel asked) => Anywhere ? asked : Channel;

    /// <summary>A clickable mention of the channel, or null when it works anywhere.</summary>
    public string? Where => Anywhere ? null : Channel?.Mention ?? "(deleted channel)";

    /// <summary>What to say when the picked channel was deleted: how to pick another.</summary>
    /// <param name="whatGoes">e.g. "Music goes" or "LFG posts go".</param>
    public string Missing(string whatGoes)
        => $"{whatGoes} in a channel that's since been deleted. A server admin can pick another with " +
           "`/settings channel`, or leave the channel out there to allow any channel.";

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
    private readonly LfgOptions _lfg;

    public ServerSettingsService(IServerSettingsStore store, IOptions<LfgOptions> lfg)
    {
        _store = store;
        _lfg = lfg.Value;
    }

    public Task<ServerSettings> GetAsync(ulong guildId) => _store.GetAsync(guildId);

    public Task UpdateAsync(ulong guildId, Action<ServerSettings> update) => _store.UpdateAsync(guildId, update);

    public async Task<bool> IsEnabledAsync(ulong guildId, ServerFeature feature)
        => (await _store.GetAsync(guildId)).IsEnabled(feature);

    public async Task<FeatureChannel> MusicChannelAsync(IGuild guild)
        => await ResolveAsync(guild, (await _store.GetAsync(guild.Id)).Music);

    public async Task<FeatureChannel> LfgChannelAsync(IGuild guild)
        => await ResolveAsync(guild, (await _store.GetAsync(guild.Id)).Lfg);

    public string DefaultPingRoleName => ConfiguredChannels.RoleName(_lfg.PingRoleName);

    /// <summary>The server's LFG playlists and ranks, or blank if it hasn't set any.</summary>
    public async Task<LfgGameProfile> LfgGameAsync(ulong guildId)
        => (await _store.GetAsync(guildId)).LfgGame ?? LfgGameProfile.Empty;

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
        ServerFeature.WebSearch => "Web search",
        ServerFeature.VoiceCommands => "Voice commands",
        ServerFeature.VoiceAutoJoin => "Voice auto-join",
        ServerFeature.MusicDj => "DJ mode",
        _ => feature.ToString(),
    };

    /// <summary>What members are told when they use a feature their server turned off.</summary>
    public static string TurnedOff(ServerFeature feature)
        => $"{NameOf(feature)} is turned off on this server. A server admin can turn it on with `/settings feature`.";

    /// <summary>The channel the server picked, or anywhere until it picks one. There's no default channel name: a
    /// server the bot has just joined won't have one, so features work everywhere until an admin chooses.</summary>
    private static async Task<FeatureChannel> ResolveAsync(IGuild guild, ChannelChoice choice)
        => choice.ChannelId is { } id
            ? new(false, await guild.GetTextChannelAsync(id, CacheMode.CacheOnly))
            : FeatureChannel.AnyChannel;
}
