namespace ModBot.Services.Polls;

/// <summary>A poll that's ready to post: a question, 2-10 distinct answers and how long it runs.</summary>
public record PollDraft(string Question, IReadOnlyList<string> Answers, int Hours, bool AllowMultiple);

/// <summary>A checked poll, or why it can't be posted.</summary>
public record PollCheck(PollDraft? Draft, string? Problem);

/// <summary>
/// Discord's limits for native polls, shared by /poll and the chatbot so both turn the same input into the same poll.
/// No Discord types: posting is <c>PollPoster</c>'s job.
/// </summary>
public static class PollRules
{
    public const int MaxQuestionLength = 300;
    public const int MaxAnswerLength = 55;
    public const int MinAnswers = 2;
    public const int MaxAnswers = 10;
    public const int MaxHours = 768; // 32 days

    /// <summary>Used when a poll is asked for without saying what the choices are ("make a poll: game night friday?").</summary>
    public static readonly IReadOnlyList<string> YesNo = ["Yes", "No"];

    /// <summary>"pizza, tacos, burgers" or "red | green | blue": a "|" anywhere means only "|" separates, so answers
    /// can contain commas ("Yes, tonight | No").</summary>
    public static IReadOnlyList<string> SplitAnswers(string text)
    {
        var separators = text.Contains('|') ? new[] { '|' } : new[] { ',', ';', '\n' };
        return text.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Checks and tidies a poll: trims everything, drops blank and repeated answers, and keeps everything
    /// within Discord's limits. Answers that are too long are an error rather than cut short, since cutting could
    /// make two answers the same or change what one means.</summary>
    public static PollCheck Check(string question, IEnumerable<string> answers, int hours, bool allowMultiple)
    {
        question = question.Trim();
        if (question.Length == 0)
        {
            return new(null, "What should the poll ask?");
        }

        if (question.Length > MaxQuestionLength)
        {
            return new(null, $"Keep the question to {MaxQuestionLength} characters.");
        }

        var kept = new List<string>();
        foreach (var answer in answers.Select(a => a.Trim()).Where(a => a.Length > 0))
        {
            if (answer.Length > MaxAnswerLength)
            {
                return new(null, $"Keep each answer to {MaxAnswerLength} characters (\"{answer[..20]}…\" is longer).");
            }

            if (!kept.Contains(answer, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(answer);
            }
        }

        if (kept.Count < MinAnswers)
        {
            return new(null, $"A poll needs at least {MinAnswers} different answers.");
        }

        if (kept.Count > MaxAnswers)
        {
            return new(null, $"A poll can have at most {MaxAnswers} answers.");
        }

        if (hours is < 1 or > MaxHours)
        {
            return new(null, $"Polls can run from 1 hour to {MaxHours / 24} days.");
        }

        return new(new PollDraft(question, kept, hours, allowMultiple), null);
    }
}
