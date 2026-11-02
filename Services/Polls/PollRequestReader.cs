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
        message = StraightenApostrophes(message);
        var phrase = CreatePhraseRegex().Match(message);
        if (!phrase.Success || IsQuestionAbout(message, phrase.Index))
        {
            return null;
        }

        int? hours = null;
        var allowMultiple = phrase.Groups["multi"].Success; // "make a multiple choice poll"

        // What comes after the phrase is the poll; if nothing does ("is game night friday? make a poll for 2 days"),
        // what came before is. The length and multiple-choice wording come off first, so they don't count as the poll.
        // The lead-in to the phrase isn't part of it either: "is game night friday? can you [make a poll]".
        var text = TakeSettings(message[(phrase.Index + phrase.Length)..], ref hours, ref allowMultiple);
        if (TakeBackRegex().IsMatch(text))
        {
            return null; // "make a poll? no wait, nvm"
        }

        var before = TrailingLeadInRegex().Replace(message[..phrase.Index], "");
        var readByRules = true;
        if (NothingLeftRegex().IsMatch(StripFiller(text)))
        {
            text = TakeSettings(before, ref hours, ref allowMultiple);
        }
        else if (!text.Contains('?') && !OptionsLabelRegex().IsMatch(text) && LastQuestion(before) is { } question)
        {
            // A question before the phrase and answers after it. It may be the poll's ("what should we play? make a
            // poll: ranked or casual") or have nothing to do with it ("are you awake? make a poll: pizza or tacos"),
            // and rules can't tell which, so the model reads it.
            text = $"{question} {StripFiller(text)}";
            readByRules = false;
        }

        // Line breaks are kept: they can separate answers.
        text = StripFiller(SpacesRegex().Replace(text, " "));
        var (laidOutQuestion, answers) = readByRules ? ReadLaidOut(text) : (null, null);
        return new PollRequest(text, hours, allowMultiple, laidOutQuestion, answers);
    }

    /// <summary>A question Yes and No don't answer: "what should we eat?", "which map?", "who's coming?".</summary>
    public static bool IsOpenQuestion(string question) => OpenQuestionRegex().IsMatch(question);

    /// <summary>True for "end my poll", "close the poll please", "can you stop this poll". Ending can't be undone, so
    /// the whole message has to be the request: "don't end my poll yet" or "who's going to close the poll?" aren't.</summary>
    public static bool IsEndRequest(string message) => EndRequestRegex().IsMatch(StraightenApostrophes(message));

    /// <summary>"end my poll": the person means their own. "end the poll" / "end that poll" might mean someone
    /// else's.</summary>
    public static bool SaysMine(string message) => MyPollRegex().IsMatch(message);

    /// <summary>Phone keyboards type "don’t"; the rules are written with "don't".</summary>
    private static string StraightenApostrophes(string message) => message.Replace('’', '\'').Replace('‘', '\'');

    /// <summary>
    /// Takes "for 2 days" and "multiple choice" off the start or end of <paramref name="text"/>, where people put
    /// them, and never out of the middle, so "who's up for 2 hours of raid tonight?" keeps its question. A question
    /// mark after one means it's part of the question ("anyone free for 2 hours?"), so it stays.
    /// </summary>
    private static string TakeSettings(string text, ref int? hours, ref bool allowMultiple)
    {
        for (var found = true; found;)
        {
            found = false;
            foreach (var regex in new[] { LeadingSettingRegex(), TrailingSettingRegex() })
            {
                if (regex.Match(text) is not { Success: true } setting)
                {
                    continue;
                }

                if (setting.Groups["length"].Success)
                {
                    hours ??= ReadHours(setting);
                }
                else
                {
                    allowMultiple = true;
                }

                text = text.Remove(setting.Index, setting.Length);
                found = true;
            }
        }

        return text;
    }

    /// <summary>
    /// The model's answers that the person actually wrote, in its order. It's told to copy them, but small models
    /// fill in options of their own ("should we play tonight?" gets "Ranked, Casual, Zombies"), and the bot would be
    /// putting words in their mouth. So an answer has to be written as one of a list: next to an "or", a comma, a "|"
    /// or a line break ("ranked or casual"), not just somewhere in the question ("should we play ranked tonight" lists
    /// nothing, so "ranked" and "tonight" are both dropped and it becomes Yes/No). Case and punctuation don't count
    /// ("Ranked!" matches "ranked"), but whole words do: "Rank" doesn't match "ranked". Repeats are dropped.
    /// </summary>
    public static IReadOnlyList<string> KeepAnswersFrom(string message, IEnumerable<string> answers)
    {
        var listed = ListMarked(message, Normalize);
        var listedAsWritten = ListMarked(message,
            t => TrailingPunctuationRegex().Replace(WhitespaceRegex().Replace(t, " ").Trim(), "").TrimStart(':', ' '));
        bool InList(string list, string answer) => list.Contains($" {answer} |") || list.Contains($"| {answer} ");

        var kept = new List<string>();
        foreach (var answer in answers.Select(a => a.Trim()).Where(a => a.Length > 0))
        {
            // Emoji and symbols have no words to compare ("🍕 or 🌮"), so those are matched as written.
            var normal = Normalize(answer);
            var written = normal.Length > 0
                ? InList(listed, normal)
                : InList(listedAsWritten, WhitespaceRegex().Replace(answer, " "));

            // Anything the model added around the words ("ranked 🔥🔥", "casual >>>") wasn't written by the person, so
            // only the words and everyday punctuation are kept. Emoji-only answers were matched exactly as written.
            var clean = normal.Length > 0
                ? WhitespaceRegex().Replace(DecorationRegex().Replace(answer, " "), " ").Trim()
                : answer;
            if (written && clean.Length > 0 && !kept.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(clean);
            }
        }

        return kept;
    }

    /// <summary>The message with each list separator ("or", ",", ";", "|", a line break) turned into " | ", then
    /// <paramref name="tidy"/> applied to each piece.</summary>
    private static string ListMarked(string message, Func<string, string> tidy)
        => " " + string.Join(" | ", ListMarkRegex().Split(message).Select(tidy)) + " ";

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

    /// <summary>
    /// "how do I make a poll", "don't make a poll yet", "did you make a poll?", "we should do a vote": talking about
    /// a poll, not asking for one. Only the part of the sentence right before the phrase counts (back to the last
    /// comma or sentence end), so "what should we play tonight? make a poll" and "hey what's up, make a poll" are
    /// still requests.
    /// </summary>
    private static bool IsQuestionAbout(string message, int phraseIndex)
    {
        var before = message[..phraseIndex];
        return QuestionLeadRegex().IsMatch(before[(before.LastIndexOfAny(['?', '.', '!', '\n', ',']) + 1)..]);
    }

    /// <summary>The question that <paramref name="text"/> ends with ("lol ok. what should we play?" → "what should
    /// we play?"), or null if it doesn't end with one.</summary>
    private static string? LastQuestion(string text)
    {
        text = text.Trim();
        if (text.Length < 2 || !text.EndsWith('?'))
        {
            return null;
        }

        var question = text[(text.LastIndexOfAny(['?', '.', '!', '\n'], text.Length - 2) + 1)..].Trim();
        return question.Length > 1 ? question : null;
    }

    private static string Normalize(string text) => WhitespaceRegex().Replace(NonWordRegex().Replace(text.ToLowerInvariant(), " "), " ").Trim();

    /// <summary>"make a poll", "can you start a quick poll", "set up a vote", "poll everyone", or a message that starts
    /// with "poll:". Not "start a vote to skip", which is the music's vote skip.</summary>
    [GeneratedRegex(@"\b(?:make|create|start|run|post|do|set\s+up|put\s+up|throw\s+up)\s+(?:me\s+|us\s+)?an?\s+(?:quick\s+|new\s+|little\s+)?(?<multi>(?:multiple[\s-]choice|multi[\s-]?select)\s+)?(?:poll|vote(?!\s+(?:to\s+)?skip))\b|\bpoll\s+(?:everyone|everybody|the\s+(?:server|chat|channel|squad|group))\b|^\W*(?:quick\s+)?poll\b\s*[:\-–]", RegexOptions.IgnoreCase)]
    private static partial Regex CreatePhraseRegex();

    /// <summary>The whole message is the request, with only a polite lead-in or "now"/"please" after. A poll, not a
    /// vote: "stop the vote" is about the music's vote skip.</summary>
    [GeneratedRegex(@"^\W*(?:(?:hey|yo|ok|okay|please|pls|can\s+you|could\s+you|would\s+you|will\s+you|go\s+ahead\s+and)[\s,]+)*(?:end|close|stop|finish|lock)\s+(?:the\s+|my\s+|this\s+|that\s+|our\s+)?poll\b(?:[\s,]+(?:now|please|pls|early|for\s+me))*[\s.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex EndRequestRegex();

    [GeneratedRegex(@"\b(?:how|where|why|what)\b|\b(?:can|could|do|may|should)\s+i\b|\bis\s+there\b|\bare\s+you\s+able\b|\b(?:don'?t|do\s+not|didn'?t|did|never|won'?t|shouldn'?t|no\s+need\s+to|not\s+going\s+to)\b|\b(?:should|could|shall)\s+we\b|\bwe\s+(?:should|could|might|can)\b|\blet'?s\s+not\b|\bi(?:'ll|'m|\s+will|\s+am|\s+might|\s+was|\s+(?:want|wanna|need|have\s+to|gotta)(?!\s+(?:you|u)\b))\b|\blet\s+me\b|\b(?:gonna|going\s+to|about\s+to|trying\s+to|tried\s+to)\b|\b(?:can'?t|cannot|can\s+not|couldn'?t|doesn'?t|isn'?t|wasn'?t)\b|\bi\s+(?:can|could)\b|\bis\s+it\s+possible\b|\b(?:does|do)\s+(?:anyone|anybody|someone|somebody|everyone)\b|\b(?:anyone|anybody|someone|somebody|who)\s+(?:wants?|wanna)\b|\b(?:if|when|once|after|before|unless)\s+(?:you|u|someone|we)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionLeadRegex();

    [GeneratedRegex(@"\bmy\s+poll\b", RegexOptions.IgnoreCase)]
    private static partial Regex MyPollRegex();

    /// <summary>Taking the request back: "make a poll? no wait", "nvm", "jk", "scratch that".</summary>
    [GeneratedRegex(@"\b(?:no\s+wait|wait\s+no|never\s*mind|nvm|jk|just\s+kidding|scratch\s+that|cancel\s+that|forget\s+it)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TakeBackRegex();

    /// <summary>"for 2 days", "for an hour", "for 3h", "for a week" (<c>length</c>), or "multiple choice", "allow
    /// multiple answers" (<c>multi</c>).</summary>
    private const string SettingPattern =
        @"(?:(?<length>for\s+(?:(?<count>\d{1,4})|an?|one)\s*(?<unit>hours?|hrs?|h|days?|d|weeks?|wks?|w))|(?<multi>multiple[\s-]choice|multi[\s-]?select|pick\s+(?:more\s+than\s+one|multiple|several)|(?:allow(?:ing)?|with)\s+multiple(?:\s+(?:answers|choices|votes))?|multiple\s+(?:answers|votes|choices)(?:\s+allowed)?))\b";

    [GeneratedRegex(@"^\s*,?\s*(?:and\s+)?" + SettingPattern + @"[\s,]*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSettingRegex();

    /// <summary>Not after "or": in "for 2 days or for a week" it's the last answer.</summary>
    [GeneratedRegex(@"(?<!\bor[\s,]*)[\s,]*(?:\band\s+)?\b" + SettingPattern + @"[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSettingRegex();

    [GeneratedRegex(@"^\W*(?:what|which|who|whom|whose|where|when|how|why)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpenQuestionRegex();

    /// <summary>"can you", "could you please", "hey", "ok so" right before the poll phrase.</summary>
    [GeneratedRegex(@"(?:\b(?:hey|yo|ok|okay|so|please|pls|(?:can|could|would|will)\s+you(?:\s+please)?)[\s,]*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingLeadInRegex();

    /// <summary>Nothing of the poll after the phrase: "?", "pls", "now please!" ("can you make a poll?").</summary>
    [GeneratedRegex(@"^[\W_]*(?:(?:pls|plz|please|now|thanks|thx|ty|for\s+me|for\s+us)\b[\W_]*)*$", RegexOptions.IgnoreCase)]
    private static partial Regex NothingLeftRegex();

    /// <summary>Words that only join the phrase to the poll: ": what should we play?", "asking if", "about".</summary>
    [GeneratedRegex(@"^(?:[:\-–,.]|asking(?:\s+(?:if|whether))?|about|on|for|to\s+see|that\s+asks|with\s+the\s+question|please)(?:\s+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFillerRegex();

    [GeneratedRegex(@"\b(?:options|choices|answers)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex OptionsLabelRegex();

    [GeneratedRegex(@"\s*(?:,\s*or\s+|,|;|\n|\s+or\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListSeparatorRegex();

    [GeneratedRegex(@"\bor\b|[,;|\n]", RegexOptions.IgnoreCase)]
    private static partial Regex ListMarkRegex();

    [GeneratedRegex(@"^[\s:\-–]+")]
    private static partial Regex LeadingPunctuationRegex();

    [GeneratedRegex(@"[\s?!.]+$")]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}&]+")]
    private static partial Regex NonWordRegex();

    /// <summary>Everything but letters, digits, spaces and everyday punctuation (S&amp;D, rock'n'roll, 1v1, C++, 3.5).</summary>
    [GeneratedRegex(@"[^\p{L}\p{N}\s&'’\-.+#/!?]+")]
    private static partial Regex DecorationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex SpacesRegex();
}
