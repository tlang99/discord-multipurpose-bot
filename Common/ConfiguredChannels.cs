using Discord;
using Discord.WebSocket;

namespace ModBot.Common;

/// <summary>
/// The default channel and role names from appsettings (e.g. <c>Music:ChannelName</c> = "music"), cleaned up and
/// matched the same way everywhere. Servers can override them with /settings; ServerSettingsService resolves which
/// applies.
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

    /// <summary>"<paramref name="whatGoes"/> in **#name**, but this server doesn't have that channel yet."</summary>
    /// <param name="whatGoes">e.g. "Music goes" or "LFG posts go".</param>
    public static string Missing(string whatGoes, string name)
        => $"{whatGoes} in **#{name}**, but this server doesn't have that channel yet.";
}
