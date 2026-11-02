using ModBot.Services.Reminders;

namespace ModBot.Tests;

public class ReminderTimeParserTests
{
    // A fixed "now" (Sunday 2026-11-01 15:00) in a fixed UTC-5 zone, so results don't depend on the clock.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "Test");
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 15, 0, 0, TimeSpan.FromHours(-5));

    [Theory]
    [InlineData("10m", "11-01 15:10", "10m")]
    [InlineData("2h30m", "11-01 17:30", "2h30m")]
    [InlineData("in 1h 15m", "11-01 16:15", "in 1h 15m")]
    [InlineData("in 3 days", "11-04 15:00", "in 3 days")]
    [InlineData("1 hour and 30 minutes", "11-01 16:30", "1 hour and 30 minutes")]
    [InlineData("half an hour", "11-01 15:30", "half an hour")]
    [InlineData("in a minute", "11-01 15:01", "in a minute")]
    [InlineData("in a couple of hours", "11-01 17:00", "in a couple of hours")]
    [InlineData("2 weeks", "11-15 15:00", "2 weeks")]
    [InlineData("at 5pm", "11-01 17:00", "at 5pm")]
    [InlineData("at 9am", "11-02 09:00", "at 9am")] // already past today, so tomorrow
    [InlineData("at 9 am", "11-02 09:00", "at 9 am")]
    [InlineData("tomorrow 9am", "11-02 09:00", "tomorrow 9am")]
    [InlineData("tomorrow at 9:30 am", "11-02 09:30", "tomorrow at 9:30 am")]
    [InlineData("tonight at 8", "11-01 20:00", "tonight at 8")]
    [InlineData("at 5", "11-01 17:00", "at 5")] // the next of 5am and 5pm
    [InlineData("at 17:45", "11-01 17:45", "at 17:45")]
    [InlineData("tomorrow", "11-02 15:00", "tomorrow")]
    [InlineData("remind me in 20 minutes to check the oven", "11-01 15:20", "in 20 minutes")]
    [InlineData("remind me 2 more times in 5 mins", "11-01 15:05", "in 5 mins")]
    public void Finds_the_time_and_the_words_that_said_it(string text, string expectedDue, string expectedPhrase)
    {
        var found = ReminderTimeParser.Find(text, Now, Zone);

        Assert.NotNull(found);
        Assert.Equal(expectedDue, TimeZoneInfo.ConvertTime(found.DueAt, Zone).ToString("MM-dd HH:mm"));
        Assert.Equal(expectedPhrase, text.Substring(found.Index, found.Length));
    }

    [Theory]
    [InlineData("remind me to buy 2 things")]
    [InlineData("in 3 months")] // months aren't supported, and mustn't be read as minutes
    [InlineData("remind me about the raid")]
    [InlineData("ABC10m")]
    [InlineData("that reminds me, the raid is tonight")] // "tonight" alone isn't a time
    public void Finds_no_time_where_there_is_none(string text)
        => Assert.Null(ReminderTimeParser.Find(text, Now, Zone));
}
