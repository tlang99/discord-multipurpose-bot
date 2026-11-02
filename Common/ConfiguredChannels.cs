namespace ModBot.Common;

/// <summary>
/// The default LFG ping role name from appsettings (<c>Lfg:PingRoleName</c>), cleaned up the same way everywhere.
/// Servers can pick their own role with /settings lfg-pings; ServerSettingsService resolves which applies. There are no
/// default channel names: each server picks its own channels with /settings channel.
/// </summary>
public static class ConfiguredChannels
{
    /// <summary>The configured role name without spaces or a leading @. Empty means the feature is off.</summary>
    public static string RoleName(string? configured) => (configured ?? "").Trim().TrimStart('@');
}
