using System.Text.RegularExpressions;
using ModBot.Common;

namespace ModBot.Services.Voice;

/// <summary>
/// Telling the bot to stop in voice ("ModBot, stop", "thanks ModBot"): it stops talking at once, and answers still on
/// their way aren't said.
/// </summary>
public static partial class VoiceGoodbye
{
    /// <summary>"Thanks", "that's all", "goodbye", "stop": the whole of what was said is a goodbye, so the bot stops
    /// talking instead of answering it. The bot's name in it ("thanks, ModBot", "ModBot, bye") doesn't count.</summary>
    public static bool IsGoodbye(string request, IEnumerable<string> names) => IsGoodbye(request, names, out _);

    /// <inheritdoc cref="IsGoodbye(string, IEnumerable{string})"/>
    /// <param name="named">Whether the bot's name was in it: only a goodbye said to it by name counts,
    /// since speech-to-text turns unclear audio into "Thank you." all too often.</param>
    public static bool IsGoodbye(string request, IEnumerable<string> names, out bool named)
    {
        named = false;
        var words = request.Replace('’', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var squashed = names.Select(TextTools.Squash).Where(n => n.Length > 0).ToHashSet();
        // The name, wherever it is, split over up to three words ("Mod Bot").
        for (var at = 0; at < words.Count; at++)
        {
            for (var take = Math.Min(3, words.Count - at); take > 0; take--)
            {
                if (squashed.Contains(string.Concat(words.Skip(at).Take(take).Select(TextTools.Squash))))
                {
                    words.RemoveRange(at, take);
                    named = true;
                    at--;
                    break;
                }
            }
        }

        return GoodbyeRegex().IsMatch(string.Join(' ', words));
    }

    /// <summary>Whether <paramref name="heard"/> is in what the bot's saying (<paramref name="saying"/>): heard over the
    /// bot, "bye!" is likely its own "bye!" coming back through a mic, not someone saying it.</summary>
    public static bool Repeats(string saying, string heard)
    {
        var words = string.Concat(Words(heard));
        return words.Length > 0 && string.Concat(Words(saying)).Contains(words, StringComparison.Ordinal);
    }

    private static List<string> Words(string text)
        => WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    [GeneratedRegex(
        @"^\W*(?:(?:ok(?:ay)?|alright|all\s+right|cool|great|perfect|awesome|nice|got\s+it|sounds\s+good|no|nope)[\s,.!]+)*" +
        @"(?:thanks?(?:\s+you)?|thank\s+you|thx|cheers|that'?s\s+(?:all|it|everything)|never\s*mind|forget\s+it|" +
        @"(?:we'?re|i'?m)\s+(?:done|good|all\s+set)|nothing(?:\s+else)?|good\s*bye|bye(?:[\s-]+bye)?|see\s+ya|" +
        @"stop(?:\s+talking)?|shut\s+up|be\s+quiet|that'?s\s+enough)" +
        @"(?:[\s,.!]+(?:so\s+much|a\s+lot|man|dude|bro|buddy|for\s+(?:that|the\s+help)|that'?s\s+(?:all|it)|bye|goodbye))*\W*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoodbyeRegex();

    [GeneratedRegex(@"[a-z0-9']+")]
    private static partial Regex WordRegex();
}
