using ModBot.Services.Chat;

namespace ModBot.Tests;

/// <summary>How long Ollama is asked to keep the model loaded: the usual keep-alive, or longer during a hold (DJ
/// mode's, until the last song ends).</summary>
public class ChatKeepAliveTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Without_a_hold_it_is_the_configured_keep_alive()
    {
        Assert.Equal(("5m", TimeSpan.FromMinutes(5)), OllamaChatService.KeepAliveFor(5, DateTime.MinValue, Now));
    }

    [Fact]
    public void Zero_leaves_it_to_Ollamas_own_five_minutes()
    {
        Assert.Equal((null, TimeSpan.FromMinutes(5)), OllamaChatService.KeepAliveFor(0, DateTime.MinValue, Now));
    }

    [Fact]
    public void A_longer_hold_asks_for_the_rest_of_it()
    {
        var (param, keptFor) = OllamaChatService.KeepAliveFor(5, Now.AddMinutes(8).AddSeconds(0.5), Now);

        Assert.Equal("481s", param);
        Assert.Equal(TimeSpan.FromSeconds(481), keptFor);
    }

    [Theory]
    [InlineData(3)] // shorter than the keep-alive
    [InlineData(-1)] // already over
    public void A_hold_shorter_than_the_keep_alive_changes_nothing(int minutesLeft)
    {
        Assert.Equal(("5m", TimeSpan.FromMinutes(5)),
            OllamaChatService.KeepAliveFor(5, Now.AddMinutes(minutesLeft), Now));
    }
}
