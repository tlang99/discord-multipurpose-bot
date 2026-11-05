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

    /// <summary>Names shorter than this many letters aren't wake words: "Dj" would go off in ordinary talk.</summary>
    public const int ShortestName = 3;

    /// <summary>
    /// What was asked after the wake word: "skip this song" from "Hey ModBot, skip this song.". Empty when they only
    /// said the wake word (the request may follow in the next sentence); null when there's no wake word at all.
    /// </summary>
    /// <param name="transcript">What speech-to-text heard.</param>
    /// <param name="names">The bot's names: its nickname in the server and its username.</param>
    /// <remarks>
    /// The name counts after a greeting anywhere in the sentence ("um, hey ModBot, play..."), or on its own at the
    /// start of a sentence ("ModBot, play..."). The name alone mid-sentence ("ModBot's so slow") is just talking about
    /// the bot. A second wake word ends the request ("hey ModBot, skip. Hey ModBot, pause." asks only to skip), so one
    /// request is never two. Names shorter than <see cref="ShortestName"/> letters are ignored: a nickname like "Dj"
    /// would go off in ordinary talk.
    /// </remarks>
    public static string? FindRequest(string transcript, IEnumerable<string> names)
    {
        var wanted = names.Select(Squash).Where(n => n.Length >= ShortestName).Distinct().ToList();
        var words = transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Count == 0 || FindName(words, wanted, 0) is not var (_, after))
        {
            return null;
        }

        var end = FindName(words, wanted, after)?.Start ?? words.Length;
        return Tidy(string.Join(' ', words[after..end]));
    }

    /// <summary>A request as said: without the comma after the name, or the full stop speech-to-text puts at the end
    /// ("play Hotel California." would search for the full stop too). A question mark or "!" is kept.</summary>
    public static string Tidy(string request)
        => request.Trim().TrimStart(',', '.', '!', '?', ':', ';', '-', ' ').TrimEnd('.', ' ');

    /// <summary>The first wake word from <paramref name="from"/> on: where it starts (its greeting, if any) and the
    /// word after the name.</summary>
    private static (int Start, int After)? FindName(string[] words, List<string> wanted, int from)
    {
        for (var start = from; start < words.Length; start++)
        {
            var afterGreeting = start > 0 && Greetings.Contains(Squash(words[start - 1]));
            var sentenceStart = start == 0 || words[start - 1][^1] is '.' or '!' or '?';
            if (!afterGreeting && !sentenceStart)
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
                    return (afterGreeting && start - 1 >= from ? start - 1 : start, end + 1);
                }
            }
        }

        return null;
    }

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
