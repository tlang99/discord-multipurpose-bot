using System.Text.RegularExpressions;

namespace ModBot.Services.Voice;

/// <summary>"@bot listen in voice" / "join the voice channel" (start) or "stop listening" / "leave voice" (stop).</summary>
public enum VoiceRequest
{
    Listen,
    StopListening,
}

/// <summary>
/// Spots a chat request to start or stop listening for voice commands, with plain rules, so it works the same typed
/// or said. The whole message has to be the request: "can you join voice later?" is a question, not a request.
/// </summary>
public static partial class VoiceRequestReader
{
    public static VoiceRequest? Read(string message)
    {
        var text = message.Replace('’', '\'').Trim();
        if (StopRegex().IsMatch(text))
        {
            return VoiceRequest.StopListening;
        }

        return ListenRegex().IsMatch(text) ? VoiceRequest.Listen : null;
    }

    private const string Polite = @"^\W*(?:(?:hey|ok|okay|please|pls|can\s+you|could\s+you|would\s+you|go\s+ahead\s+and)[\s,]+)*";
    private const string End = @"(?:[\s,]+(?:now|please|pls))*[\s.!]*$";
    private const string Voice = @"(?:the\s+|our\s+|my\s+)?(?:voice(?:\s+(?:channel|chat))?|vc|call)";

    /// <summary>"listen in voice", "join voice", "join the vc and listen", "come listen", "start listening".</summary>
    [GeneratedRegex(Polite +
        @"(?:(?:start\s+)?listen(?:ing)?(?:\s+(?:in|to)\s+" + Voice + @")?(?:\s+for\s+(?:voice\s+)?commands)?" +
        @"|(?:join|come\s+(?:in|into|to))\s+" + Voice + @"(?:\s+and\s+listen)?" +
        @"|come\s+listen)" + End, RegexOptions.IgnoreCase)]
    private static partial Regex ListenRegex();

    /// <summary>"stop listening", "leave voice", "leave the vc", "stop voice commands".</summary>
    [GeneratedRegex(Polite +
        @"(?:stop\s+listening(?:\s+(?:in|to)\s+" + Voice + @")?|leave\s+" + Voice + @"|stop\s+voice\s+commands)" + End,
        RegexOptions.IgnoreCase)]
    private static partial Regex StopRegex();
}
