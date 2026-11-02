using System.Text.RegularExpressions;

namespace ModBot.Services.Polls;

/// <summary>A poll asked for in chat.</summary>
/// <param name="Text">The request with the poll phrase, length and multiple-choice wording taken out ("what should we
/// play tonight? ranked or casual"), for the model to read when <see cref="Question"/> is null.</param>
/// <param name="Hours">How long it should run, if the message said ("for 2 days").</param>
/// <param name="Question">Set, with <see cref="Answers"/>, when the message laid the poll out plainly enough to read
/// without the model ("question? a, b, c" or "question options: a, b").</param>
public record PollRequest(string Text, int? Hours, bool AllowMultiple, string? Question = null,
    IReadOnlyList<string>? Answers = null);

/// <summary>
/// Spots poll requests in chat ("make a poll: what should we play? ranked, casual or zombies") and "end my poll", with
/// plain rules, like <see cref="ModBot.Services.Reminders.ReminderRequestReader"/>. A poll phrase is unambiguous, so
/// the model's request check isn't given a fifth answer (adding reminders as a fourth made it worse at the others).
/// The model is only asked to pull out the question and answers when the message doesn't lay them out plainly, and
/// <see cref="KeepAnswersFrom"/> stops it adding answers nobody wrote.
/// </summary>
public static partial class PollRequestReader
{
    /// <summary>The poll <paramref name="message"/> asks for, or null if it isn't a poll request. Questions about
    /// polls ("how do I make a poll?") aren't requests.</summary>
    public static PollRequest? Read(string message)
    {
        var phrase = CreatePhraseRegex().Match(message);
        if (!phrase.Success || IsQuestionAbout(message, phrase.Index))
        {
            return null;
        }

        // What comes after the phrase is the poll; if nothing does ("is game night friday? make a poll"), what came
        // before is.
        var text = message[(phrase.Index + phrase.Length)..];
        if (string.IsNullOrWhiteSpace(StripFiller(text)))
        {
            text = message[..phrase.Index];
        }

        int? hours = null;
        if (LengthRegex().Match(text) is { Success: true } length)
        {
            hours = ReadHours(length);
            text = text.Remove(length.Index, length.Length);
        }

        var allowMultiple = false;
        if (MultipleRegex().Match(text) is { Success: true } multiple)
        {
            allowMultiple = true;
            text = text.Remove(multiple.Index, multiple.Length);
        }

        // Line breaks are kept: they can separate answers.
        text = StripFiller(SpacesRegex().Replace(text, " "));
        var (question, answers) = ReadLaidOut(text);
        return new PollRequest(text, hours, allowMultiple, question, answers);
    }

    /// <summary>True for "end my poll", "close the poll", "stop this vote".</summary>
    public static bool IsEndRequest(string message)
        => EndPhraseRegex().Match(message) is { Success: true } phrase && !IsQuestionAbout(message, phrase.Index);

    /// <summary>
    /// The model's answers that the person actually wrote, in its order. It's told to copy them, but small models
    /// fill in options of their own ("should we play tonight?" gets "Ranked, Casual, Zombies"), and the bot would be
    /// putting words in their mouth. Case, spacing and punctuation don't count ("Ranked!" matches "ranked"), but the
    /// words themselves do, whole: "Rank" doesn't match "ranked".
    /// </summary>
    public static IReadOnlyList<string> KeepAnswersFrom(string message, IEnumerable<string> answers)
    {
        var written = $" {Normalize(message)} ";
        return answers
            .Select(a => a.Trim())
            .Where(a => Normalize(a) is { Length: > 0 } normal && written.Contains($" {normal} "))
            .ToList();
    }

    /// <summary>"question? a, b or c", "question options: a, b", or nothing to go on.</summary>
    private static (string? Question, IReadOnlyList<string>? Answers) ReadLaidOut(string text)
    {
        string question, rest;
        if (OptionsLabelRegex().Match(text) is { Success: true } label)
        {
            (question, rest) = (text[..label.Index], text[(label.Index + label.Length)..]);
        }
        else if (text.IndexOf('?') is var mark and >= 0)
        {
            (question, rest) = (text[..(mark + 1)], text[(mark + 1)..]);
        }
        else
        {
            return (null, null);
        }

        question = question.Trim().TrimEnd(':', '-', '–', ',').Trim();
        var answers = SplitList(LeadingPunctuationRegex().Replace(rest, ""));
        return question.Length > 0 && answers.Count >= PollRules.MinAnswers ? (question, answers) : (null, null);
    }

    /// <summary>Like <see cref="PollRules.SplitAnswers"/>, plus "or" between answers, as people write in chat: "ranked,
    /// casual or zombies".</summary>
    private static IReadOnlyList<string> SplitList(string text)
    {
        if (text.Contains('|'))
        {
            return PollRules.SplitAnswers(text);
        }

        return ListSeparatorRegex().Split(text)
            .Select(a => TrailingPunctuationRegex().Replace(a.Trim(), ""))
            .Where(a => a.Length > 0)
            .ToList();
    }

    private static int ReadHours(Match length)
    {
        var count = length.Groups["count"].Success ? int.Parse(length.Groups["count"].Value) : 1;
        var unit = length.Groups["unit"].Value.ToLowerInvariant();
        var perUnit = unit.StartsWith('w') ? 24 * 7 : unit.StartsWith('d') ? 24 : 1;
        return (int)Math.Min((long)count * perUnit, int.MaxValue);
    }

    private static string StripFiller(string text)
    {
        text = text.Trim();
        for (var previous = ""; previous != text;)
        {
            previous = text;
            text = LeadingFillerRegex().Replace(text, "").Trim();
        }

        return text;
    }

    /// <summary>"how do I make a poll", "can I end my poll": asking about it, not asking for it.</summary>
    private static bool IsQuestionAbout(string message, int phraseIndex)
        => QuestionLeadRegex().IsMatch(message[..phraseIndex]);

    private static string Normalize(string text) => WhitespaceRegex().Replace(NonWordRegex().Replace(text.ToLowerInvariant(), " "), " ").Trim();

    /// <summary>"make a poll", "can you start a quick poll", "set up a vote", "poll everyone", or a message that starts
    /// with "poll:". Not "start a vote to skip", which is the music's vote skip.</summary>
    [GeneratedRegex(@"\b(?:make|create|start|run|post|do|set\s+up|put\s+up|throw\s+up)\s+(?:me\s+|us\s+)?an?\s+(?:quick\s+|new\s+|little\s+)?(?:poll|vote(?!\s+(?:to\s+)?skip))\b|\bpoll\s+(?:everyone|everybody|the\s+(?:server|chat|channel|squad|group))\b|^\W*(?:quick\s+)?poll\b\s*[:\-–]", RegexOptions.IgnoreCase)]
    private static partial Regex CreatePhraseRegex();

    [GeneratedRegex(@"\b(?:end|close|stop|finish|lock)\s+(?:the\s+|my\s+|this\s+|that\s+|our\s+)?(?:poll|vote)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EndPhraseRegex();

    [GeneratedRegex(@"\b(?:how|where|why|what)\b|\b(?:can|could|do|may|should)\s+i\b|\bis\s+there\b|\bare\s+you\s+able\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionLeadRegex();

    /// <summary>"for 2 days", "for an hour", "for 3h", "for a week".</summary>
    [GeneratedRegex(@"\bfor\s+(?:(?<count>\d{1,4})|an?|one)\s*(?<unit>hours?|hrs?|h|days?|d|weeks?|wks?|w)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LengthRegex();

    [GeneratedRegex(@"\b(?:multiple[\s-]choice|multi[\s-]?select|pick\s+(?:more\s+than\s+one|multiple|several)|(?:allow(?:ing)?|with)\s+multiple(?:\s+(?:answers|choices|votes))?|multiple\s+(?:answers|votes|choices)(?:\s+allowed)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MultipleRegex();

    /// <summary>Words that only join the phrase to the poll: ": what should we play?", "asking if", "about".</summary>
    [GeneratedRegex(@"^(?:[:\-–,.]|asking(?:\s+(?:if|whether))?|about|on|for|to\s+see|that\s+asks|with\s+the\s+question|please)(?:\s+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFillerRegex();

    [GeneratedRegex(@"\b(?:options|choices|answers)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex OptionsLabelRegex();

    [GeneratedRegex(@"\s*(?:,\s*or\s+|,|;|\n|\s+or\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListSeparatorRegex();

    [GeneratedRegex(@"^[\s:\-–]+")]
    private static partial Regex LeadingPunctuationRegex();

    [GeneratedRegex(@"[\s?!.]+$")]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}&]+")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex SpacesRegex();
}
