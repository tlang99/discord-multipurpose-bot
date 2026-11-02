using System.Globalization;
using System.Text.RegularExpressions;

namespace ModBot.Services.Reminders;

/// <summary>A time found in some text: when it is, and which part of the text said so (for removing it).</summary>
public record ReminderTime(DateTimeOffset DueAt, int Index, int Length);

/// <summary>
/// Reads when a reminder is due from text like "10m", "in 2 hours", "1h30m", "half an hour", "at 5pm",
/// "tomorrow 9am" or "tonight at 8". Plain rules rather than the model: small models are unreliable with numbers,
/// and the same reader serves /remind and the chatbot. Clock times are read in the given time zone.
/// </summary>
public static partial class ReminderTimeParser
{
    private static readonly Dictionary<string, double> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30,
        ["forty"] = 40, ["forty-five"] = 45, ["fifty"] = 50, ["sixty"] = 60, ["ninety"] = 90,
        ["half a"] = 0.5, ["half an"] = 0.5, ["a couple of"] = 2, ["a couple"] = 2, ["a few"] = 3,
    };

    /// <summary>Finds the first time in <paramref name="text"/>, or null if there isn't one it understands. A
    /// relative time ("in 2 hours") wins over a clock time if both appear.</summary>
    public static ReminderTime? Find(string text, DateTimeOffset now, TimeZoneInfo timeZone)
        => FindRelative(text, now) ?? FindClockTime(text, now, timeZone);

    private static ReminderTime? FindRelative(string text, DateTimeOffset now)
    {
        var first = DurationPartRegex().Match(text);
        if (!first.Success)
        {
            return null;
        }

        // "1h30m", "2 hours 15 minutes", "1 hour and 30 minutes": keep adding parts that follow straight on.
        var total = TimeSpan.Zero;
        var end = first.Index;
        for (var match = first; match.Success; match = DurationPartRegex().Match(text, end))
        {
            if (match.Index != end && !JoinerRegex().IsMatch(text[end..match.Index]))
            {
                break;
            }

            if (ToDuration(match) is not { } part)
            {
                break;
            }

            total += part;
            end = match.Index + match.Length;
        }

        if (total <= TimeSpan.Zero)
        {
            return null;
        }

        // Include a leading "in " so the chatbot can strip "in 20 minutes" as a whole.
        var start = first.Index;
        var before = text[..start];
        if (InSuffixRegex().Match(before) is { Success: true } inWord)
        {
            start = inWord.Index;
        }

        return new ReminderTime(now + total, start, end - start);
    }

    private static TimeSpan? ToDuration(Match match)
    {
        var amountText = match.Groups["amount"].Value.Trim();
        double amount;
        if (!double.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out amount) &&
            !NumberWords.TryGetValue(WhitespaceRegex().Replace(amountText, " "), out amount))
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit[0] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' when unit.StartsWith("mo") => null, // "months" isn't supported; don't read it as minutes
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(7 * amount),
            _ => null,
        };
    }

    private static ReminderTime? FindClockTime(string text, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        var clock = ClockRegex().Match(text);
        var dayWord = DayWordRegex().Match(text);
        if (!clock.Success)
        {
            // "tomorrow" on its own: this time tomorrow.
            return dayWord.Success && dayWord.Value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase)
                ? new ReminderTime(now.AddDays(1), dayWord.Index, dayWord.Length)
                : null;
        }

        var hour = int.Parse(clock.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = clock.Groups["minute"].Success ? int.Parse(clock.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = clock.Groups["ampm"].Value.ToLowerInvariant();
        if (minute > 59 || hour > 23 || (meridiem.Length > 0 && (hour is 0 or > 12)))
        {
            return null;
        }

        var day = dayWord.Success ? dayWord.Value.ToLowerInvariant() : null;
        if (meridiem.StartsWith('p') && hour < 12) hour += 12;
        if (meridiem.StartsWith('a') && hour == 12) hour = 0;

        // "tonight at 8" means 8pm.
        if (meridiem.Length == 0 && day == "tonight" && hour < 12) hour += 12;

        var date = localNow.Date.AddDays(day == "tomorrow" ? 1 : 0);
        DateTime candidate;
        if (meridiem.Length == 0 && hour is >= 1 and <= 12 && day is null)
        {
            // "at 5" with no am/pm and no day: whichever of 5am / 5pm comes next.
            var morning = date.AddHours(hour % 12).AddMinutes(minute);
            var evening = morning.AddHours(12);
            candidate = morning > localNow.DateTime ? morning : evening > localNow.DateTime ? evening : morning.AddDays(1);
        }
        else
        {
            candidate = date.AddHours(hour).AddMinutes(minute);
            if (day is null && candidate <= localNow.DateTime)
            {
                candidate = candidate.AddDays(1); // "at 9am" after 9am means tomorrow
            }
        }

        var due = new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate));

        // Report the whole phrase ("tomorrow at 9am") so the chatbot can strip it.
        var start = clock.Index;
        var end = clock.Index + clock.Length;
        if (dayWord.Success)
        {
            start = Math.Min(start, dayWord.Index);
            end = Math.Max(end, dayWord.Index + dayWord.Length);
        }

        return new ReminderTime(due, start, end - start);
    }

    /// <summary>Digits may touch the unit and each other's units ("10m", "2h30m"); number words need a space and a
    /// full unit word, so "9 am" isn't read as "a m(inute)". Digits may follow only a unit letter (h, m, s, d, w), so
    /// "2h30m" chains but "abc10m" isn't a time.</summary>
    [GeneratedRegex(@"(?:(?<![\d.a-ce-gi-lnoq-rt-vx-z])(?<amount>\d+(?:\.\d+)?)\s*(?<unit>seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|wks?|w|months?)(?![a-z])|(?<![\w.])(?<amount>half\s+an?|a\s+couple(?:\s+of)?|a\s+few|an?|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty-five|forty|fifty|sixty|ninety)\s+(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?|wks?|months?)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPartRegex();

    [GeneratedRegex(@"^\s*(?:,|and|&)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex JoinerRegex();

    [GeneratedRegex(@"\bin\s+$", RegexOptions.IgnoreCase)]
    private static partial Regex InSuffixRegex();

    /// <summary>A clock time needs "at", am/pm or ":MM" so a bare number ("remind me 2 things") isn't read as one.</summary>
    [GeneratedRegex(@"(?:\bat\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>[ap]\.?m\.?)?|\b(?<hour>\d{1,2})(?::(?<minute>\d{2}))\s*(?<ampm>[ap]\.?m\.?)?|\b(?<hour>\d{1,2})\s*(?<ampm>[ap]\.?m\.?))(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"\b(?:today|tonight|tomorrow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
