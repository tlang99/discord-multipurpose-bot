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

        var on = (match.Groups["verb"].Value + match.Groups["state"].Value).ToLowerInvariant() switch
        {
            var v when v.Contains("enable") || v.EndsWith("on") => true,
            _ => false,
        };

        var name = match.Groups["feature"].Value.ToLowerInvariant();
        ServerFeature? feature = name switch
        {
            _ when name.Contains("action") => ServerFeature.ChatActions,
            _ when name.StartsWith("chat") || name.Contains("bot") || name.Contains("ai") => ServerFeature.Chat,
            _ when name.StartsWith("music") => ServerFeature.Music,
            _ when name.StartsWith("lfg") || name.StartsWith("looking") => ServerFeature.Lfg,
            _ when name.StartsWith("poll") => ServerFeature.Polls,
            _ when name.StartsWith("remind") => ServerFeature.Reminders,
            _ => null,
        };

        return feature is { } f ? new FeatureSwitchRequest(f, on) : null;
    }

    private const string Feature =
        @"(?<feature>chat\s*bot|the\s+bot\s+chat|ai\s+chat|chat\s+actions|actions|chat|music|lfgs?|looking\s+for\s+group|polls?|reminders?)";

    private const string Here = @"(?:for\s+(?:this|the)\s+server|on\s+this\s+server|server[\s-]?wide|here|feature)";

    /// <summary>"enable/disable X [here]" or "turn/switch X on/off for this server" / "turn/switch on/off X here".</summary>
    [GeneratedRegex(
        @"^\W*(?:(?:hey|ok|okay|please|pls|can\s+you|could\s+you|would\s+you|go\s+ahead\s+and)[\s,]+)*" +
        @"(?:(?<verb>enable|disable)\s+(?:the\s+)?" + Feature + @"(?:\s+" + Here + @")?" +
        @"|(?<verb>turn|switch)\s+(?<state>on|off)\s+(?:the\s+)?" + Feature + @"\s+" + Here +
        @"|(?<verb>turn|switch)\s+(?:the\s+)?" + Feature + @"\s+(?<state>on|off)\s+" + Here + @")" +
        @"(?:[\s,]+(?:now|please|pls))*[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestRegex();
}
