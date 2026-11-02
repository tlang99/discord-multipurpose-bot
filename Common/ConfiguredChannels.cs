using Discord;
using Discord.WebSocket;

namespace ModBot.Common;

/// <summary>
/// Channels and roles a feature is tied to by name in appsettings (e.g. <c>Music:ChannelName</c> = "music"). Every
/// feature reads its names through here, so they're all cleaned up and matched the same way.
/// </summary>
public static class ConfiguredChannels
{
    /// <summary>The configured channel name without spaces or a leading #. Empty means the feature isn't limited to
    /// one channel.</summary>
    public static string ChannelName(string? configured) => (configured ?? "").Trim().TrimStart('#');

    /// <summary>The configured role name without spaces or a leading @. Empty means the feature is off.</summary>
    public static string RoleName(string? configured) => (configured ?? "").Trim().TrimStart('@');

    public static bool IsNamed(IChannel channel, string name)
        => channel.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The text channel called <paramref name="name"/>, or null if there's none (or no name is set).</summary>
    public static ITextChannel? Find(SocketGuild guild, string name)
        => name.Length == 0 ? null : guild.TextChannels.FirstOrDefault(c => IsNamed(c, name));

    /// <summary>Where a feature's posts go: the configured channel, or <paramref name="fallback"/> when the feature
    /// isn't limited to one. Null when a channel is configured but the server doesn't have it.</summary>
    public static ITextChannel? FindOr(SocketGuild guild, string name, ITextChannel fallback)
        => name.Length == 0 ? fallback : Find(guild, name);

    public static IRole? FindRole(SocketGuild guild, string name)
        => name.Length == 0
            ? null
            : guild.Roles.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"<paramref name="whatGoes"/> in **#name**, but this server doesn't have that channel yet."</summary>
    /// <param name="whatGoes">e.g. "Music goes" or "LFG posts go".</param>
    public static string Missing(string whatGoes, string name)
        => $"{whatGoes} in **#{name}**, but this server doesn't have that channel yet.";
}
