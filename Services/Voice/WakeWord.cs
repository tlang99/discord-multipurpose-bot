using System.Text;

namespace ModBot.Services.Voice;

/// <summary>
/// Finds the wake word ("hey ModBot") in what someone said in voice, and what they asked after it. Speech-to-text
/// spells the name however it heard it ("Mod Bot", "mod-bot", "ModBot,"), so names are compared with spaces,
/// punctuation and case ignored, across up to three words.
/// </summary>
public static class WakeWord
{
    /// <summary>Words that can come right before the name ("hey ModBot", "ok ModBot").</summary>
    private static readonly HashSet<string> Greetings = ["hey", "hi", "ok", "okay", "yo", "hello"];

    private const int MaxWordsPerName = 3;

    /// <summary>
    /// What was asked after the wake word: "skip this song" from "Hey ModBot, skip this song.". Empty when they only
    /// said the wake word (the request may follow in the next sentence); null when there's no wake word at all.
    /// </summary>
    /// <param name="transcript">What speech-to-text heard.</param>
    /// <param name="names">The bot's names: its nickname in the server and its username.</param>
    /// <remarks>The name counts after a greeting anywhere in the sentence ("um, hey ModBot, play..."), or on its own
    /// as the first word ("ModBot, play..."). The name alone mid-sentence ("ModBot's so slow") is just talking about
    /// the bot.</remarks>
    public static string? FindRequest(string transcript, IEnumerable<string> names)
    {
        var wanted = names.Select(Squash).Where(n => n.Length > 0).Distinct().ToList();
        var words = transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Count == 0 || words.Length == 0)
        {
            return null;
        }

        for (var start = 0; start < words.Length; start++)
        {
            var afterGreeting = start > 0 && Greetings.Contains(Squash(words[start - 1]));
            if (start > 0 && !afterGreeting)
            {
                continue;
            }

            // The name can be split over up to three words ("Mod Bot").
            var joined = "";
            for (var end = start; end < Math.Min(words.Length, start + MaxWordsPerName); end++)
            {
                joined += Squash(words[end]);
                if (wanted.Contains(joined))
                {
                    return Rest(words, end + 1);
                }
            }
        }

        return null;
    }

    /// <summary>The words after the name, without the comma that followed it or the full stop speech-to-text puts at
    /// the end ("play Hotel California." would search for the full stop too). A question mark is kept.</summary>
    private static string Rest(string[] words, int from)
        => string.Join(' ', words.Skip(from)).TrimStart(',', '.', '!', '?', ':', ';', '-', ' ').TrimEnd('.', ' ');

    /// <summary>Letters and digits only, lower case: "Mod-Bot," is "modbot". ("ModBot's" is "modbots", so "ModBot's so
    /// slow" isn't taken as a request.)</summary>
    private static string Squash(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
