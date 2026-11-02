namespace ModBot.Models;

/// <summary>A feature a server can turn off for itself with /settings feature.</summary>
/// <remarks>Saved by name (see <see cref="ServerSettings.DisabledFeatures"/>), so reordering these is safe.</remarks>
public enum ServerFeature
{
    /// <summary>The chatbot answering @mentions and replies.</summary>
    Chat,

    /// <summary>The chatbot doing things it's asked ("play X", "make a poll"), not just talking.</summary>
    ChatActions,
    Music,
    Lfg,
    Polls,
    Reminders,
}

/// <summary>
/// One server's own choices for the community features, set with /settings: which features are on and which channels
/// and role they use. Anything left unset uses the bot's defaults from appsettings.json, so a new server works out of
/// the box. Kept apart from <see cref="GuildSettings"/> (moderation), as that class asks.
/// </summary>
public class ServerSettings
{
    public ulong GuildId { get; set; }

    /// <summary>Features this server has turned off, by <see cref="ServerFeature"/> name. Everything else is on.</summary>
    public List<string> DisabledFeatures { get; set; } = [];

    public ChannelChoice Music { get; set; } = new();

    public ChannelChoice Lfg { get; set; } = new();

    /// <summary>The role new LFG posts ping. Null uses the default (<c>Lfg:PingRoleName</c>, made by /lfg-setup).</summary>
    public ulong? LfgPingRoleId { get; set; }

    /// <summary>This server turned LFG pings off.</summary>
    public bool LfgPingsOff { get; set; }

    /// <remarks>Null-safe: a hand-edited file with <c>"DisabledFeatures": null</c> means nothing is off.</remarks>
    public bool IsEnabled(ServerFeature feature) => DisabledFeatures?.Contains(feature.ToString()) != true;
}

/// <summary>Where a feature works in one server: the channel it picked, or any channel until it picks one.</summary>
public class ChannelChoice
{
    /// <summary>Only this channel; null means any channel. Saved by ID, so renaming the channel doesn't break it.</summary>
    public ulong? ChannelId { get; set; }
}
