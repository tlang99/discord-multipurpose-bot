using ModBot.Services.Reminders;

namespace ModBot.Tests;

public class ReminderRequestReaderTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-5), "Test", "Test");
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 15, 0, 0, TimeSpan.FromHours(-5));

    [Theory]
    [InlineData("remind me in 20 minutes to check the oven", "check the oven")]
    [InlineData("set a reminder for 5pm to start the raid", "start the raid")]
    [InlineData("in 2 hours remind me that the raid starts", "the raid starts")]
    [InlineData("ping me at 9pm about ranked", "about ranked")]
    [InlineData("remind me tomorrow to update my loadout", "update my loadout")]
    [InlineData("can you please remind me in 1h30m to stretch?", "stretch")]
    [InlineData("remind me in 5 minutes to make an lfg", "make an lfg")]
    [InlineData("remind me in 10 minutes", "")]
    public void Reads_requests(string message, string expectedText)
    {
        var request = ReminderRequestReader.Read(message, Now, Zone);

        Assert.NotNull(request);
        Assert.Equal(expectedText, request.Value.Text);
    }

    [Theory]
    [InlineData("can you remind me how to make an lfg?")] // no time
    [InlineData("that reminds me, the raid is tonight")]
    [InlineData("remind me what the patch notes said")]
    [InlineData("remind me to eat")]
    [InlineData("the raid is at 9pm")] // a time, but nobody asked for a reminder
    public void Ignores_non_requests(string message) => Assert.Null(ReminderRequestReader.Read(message, Now, Zone));
}
