using System.Text.RegularExpressions;
using ModBot.Models;

namespace ModBot.Services.Settings;

/// <summary>A chat request to turn a feature on or off for the server.</summary>
public record FeatureSwitchRequest(ServerFeature Feature, bool On);

/// <summary>
/// Spots "@bot disable polls", "enable the chatbot", "turn music off for this server" in chat, with plain rules. The
/// whole message has to be the request, and "turn on/off" needs "for this server" (or "here", "feature"): "turn off
/// the music" is someone asking to stop the song, not to switch music off for everyone.
/// </summary>
public static partial class SettingsRequestReader
{
    public static FeatureSwitchRequest? Read(string message)
    {
        var match = RequestRegex().Match(message.Replace('’', '\''));
        if (!match.Success)
        {
            return null;
        }

        var verb = match.Groups["verb"].Value.ToLowerInvariant();
        var on = verb == "enable" || match.Groups["state"].Value.Equals("on", StringComparison.OrdinalIgnoreCase);

        // Whitespace-normalised, so "chat  bot" is "chat bot"; the regex only lets these names through.
        var name = WhitespaceRegex().Replace(match.Groups["feature"].Value.ToLowerInvariant(), " ");
        ServerFeature? feature = AutoJoinRegex().IsMatch(name) ? ServerFeature.VoiceAutoJoin : name switch
        {
            "chat actions" or "actions" => ServerFeature.ChatActions,
            "chatbot" or "chat bot" or "the bot chat" or "ai chat" or "chat" => ServerFeature.Chat,
            "music" => ServerFeature.Music,
            "lfg" or "lfgs" or "looking for group" => ServerFeature.Lfg,
            "poll" or "polls" => ServerFeature.Polls,
            "reminder" or "reminders" => ServerFeature.Reminders,
            "voice commands" or "voice command" or "voice control" => ServerFeature.VoiceCommands,
            _ => null,
        };

        // "Disable music" could just mean "stop the song", and switching music off for everyone also stops what's
        // playing, so that one needs saying plainly: "disable music for this server". The turn on/off forms always
        // need it already.
        if (feature == ServerFeature.Music && verb == "disable" && !match.Groups["here"].Success)
        {
            return null;
        }

        return feature is { } f ? new FeatureSwitchRequest(f, on) : null;
    }

    private const string Feature =
        @"(?<feature>(?:voice\s+)?auto[\s-]?join(?:ing)?(?:\s+voice)?|chat\s*bot|the\s+bot\s+chat|ai\s+chat|chat\s+actions|actions|chat|music|lfgs?|looking\s+for\s+group|polls?|reminders?|voice\s+commands?|voice\s+control)";

    private const string Here = @"(?:for\s+(?:this|the)\s+server|on\s+this\s+server|server[\s-]?wide|here|feature)";

    /// <summary>"enable/disable X [here]" or "turn/switch X on/off for this server" / "turn/switch on/off X here".</summary>
    [GeneratedRegex(
        @"^\W*(?:(?:hey|ok|okay|please|pls|can\s+you|could\s+you|would\s+you|go\s+ahead\s+and)[\s,]+)*" +
        @"(?:(?<verb>enable|disable)\s+(?:the\s+)?" + Feature + @"(?<here>\s+" + Here + @")?" +
        @"|(?<verb>turn|switch)\s+(?<state>on|off)\s+(?:the\s+)?" + Feature + @"\s+" + Here +
        @"|(?<verb>turn|switch)\s+(?:the\s+)?" + Feature + @"\s+(?<state>on|off)\s+" + Here + @")" +
        @"(?:[\s,]+(?:now|please|pls))*[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestRegex();

    /// <summary>The ways of naming voice auto-join ("voice auto-join", "auto joining", "auto-join voice"...), whole.</summary>
    [GeneratedRegex(@"^(?:voice )?auto[ -]?join(?:ing)?(?: voice)?$")]
    private static partial Regex AutoJoinRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
