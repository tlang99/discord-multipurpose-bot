namespace ModBot.Models;

/// <summary>A feature a server can turn off (or, for opt-in ones, on) for itself with /settings feature.</summary>
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

    /// <summary>Looking things up on the web (/search, "@bot search for …"). Only the search words are sent off the
    /// machine, to Ollama's web search; works once whoever runs the bot sets a key.</summary>
    WebSearch,

    /// <summary>Listening in voice for "hey ModBot". Off until a server turns it on (<see cref="ServerSettings.IsOptIn"/>),
    /// since it means the bot hearing the voice channel.</summary>
    VoiceCommands,

    /// <summary>With voice commands on, joining voice by itself when people are in a voice channel, so "hey ModBot" is
    /// all it takes. Off until a server turns it on as well (<see cref="ServerSettings.IsOptIn"/>): enabling voice
    /// commands shouldn't also start the bot sitting in every call.</summary>
    VoiceAutoJoin,

    /// <summary>DJ mode: when the music queue runs out, the chatbot picks songs like the recent ones so the music keeps
    /// going (<see cref="Modules.Music.DjPicker"/>). Off until a server turns it on
    /// (<see cref="ServerSettings.IsOptIn"/>).</summary>
    MusicDj,
}

/// <summary>
/// One server's own choices for the community features, set with /settings: which features are on and which channels
/// and role they use. Anything left unset uses the bot's defaults from appsettings.json, so a new server works out of
/// the box. Kept apart from <see cref="GuildSettings"/> (moderation), as that class asks.
/// </summary>
public class ServerSettings
{
    public ulong GuildId { get; set; }

    /// <summary>Features this server has turned off, by <see cref="ServerFeature"/> name. Everything else is on, apart
    /// from the opt-in ones (<see cref="IsOptIn"/>).</summary>
    public List<string> DisabledFeatures { get; set; } = [];

    /// <summary>Opt-in features (<see cref="IsOptIn"/>) this server has turned on, by name.</summary>
    public List<string> EnabledFeatures { get; set; } = [];

    public ChannelChoice Music { get; set; } = new();

    public ChannelChoice Lfg { get; set; } = new();

    /// <summary>The one voice channel the bot joins by itself (voice auto-join); null means whichever one people are
    /// in (/settings voice-channel).</summary>
    public ChannelChoice AutoJoinChannel { get; set; } = new();

    /// <summary>In voice, after the bot answers, anyone in the channel can say the next thing without its name, not
    /// just whoever asked (/settings voice-follow-ups). Off by default: with several people talking, the bot would
    /// answer their chatter with each other.</summary>
    public bool VoiceFollowUpsFromAnyone { get; set; }

    /// <summary>The role new LFG posts ping. Null uses the default (<c>Lfg:PingRoleName</c>, made by /lfg-setup).</summary>
    public ulong? LfgPingRoleId { get; set; }

    /// <summary>This server turned LFG pings off.</summary>
    public bool LfgPingsOff { get; set; }

    /// <summary>The game this server's LFG posts are for: its playlists and ranks (/settings lfg-game). Null = blank, so
    /// LFG works without game-specific options until an admin fills them in.</summary>
    public LfgGameProfile? LfgGame { get; set; }

    /// <remarks>Null-safe: a hand-edited file with <c>"DisabledFeatures": null</c> means nothing is off (and
    /// <c>"EnabledFeatures": null</c> means no opt-in feature is on).</remarks>
    public bool IsEnabled(ServerFeature feature)
        => IsOptIn(feature)
            ? EnabledFeatures?.Contains(feature.ToString()) == true
            : DisabledFeatures?.Contains(feature.ToString()) != true;

    /// <summary>Features that start off and have to be turned on: voice commands and voice auto-join, since they mean
    /// the bot listening, and DJ mode, since it plays songs nobody asked for.</summary>
    public static bool IsOptIn(ServerFeature feature)
        => feature is ServerFeature.VoiceCommands or ServerFeature.VoiceAutoJoin or ServerFeature.MusicDj;

    /// <summary>The bot joins voice by itself here: voice auto-join is on, and so is everything it needs (voice
    /// commands, and the chatbot that every voice request goes through).</summary>
    public bool AutoJoinsVoice
        => IsEnabled(ServerFeature.Chat) && IsEnabled(ServerFeature.VoiceCommands) &&
           IsEnabled(ServerFeature.VoiceAutoJoin);

    /// <summary>Turns <paramref name="feature"/> on or off, in whichever list it's kept.</summary>
    public void Set(ServerFeature feature, bool on)
    {
        var name = feature.ToString();
        var list = IsOptIn(feature) ? EnabledFeatures ??= [] : DisabledFeatures ??= [];
        list.Remove(name);
        if (on == IsOptIn(feature))
        {
            list.Add(name);
        }
    }
}

/// <summary>Where a feature works in one server: the channel it picked, or any channel until it picks one.</summary>
public class ChannelChoice
{
    /// <summary>Only this channel; null means any channel. Saved by ID, so renaming the channel doesn't break it.</summary>
    public ulong? ChannelId { get; set; }
}
