using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Chat;
using ModBot.Services.Reminders;

namespace ModBot.Tests;

public sealed class ChatActionParserTests : IDisposable
{
    private readonly TempReminderStore _temp = new();

    public void Dispose() => _temp.Dispose();

    private ChatActionParser Parser(FakeChat chat)
        => new(chat, new ReminderService(_temp.Store, new FakeGuildSettings(), Options.Create(new ReminderOptions()),
            NullLogger<ReminderService>.Instance), NullLogger<ChatActionParser>.Instance);

    [Theory]
    [InlineData("Play, stay fly.", "Play stay fly.")] // a pause after "play", written down as a comma
    [InlineData("play - mr brightside", "play mr brightside")]
    [InlineData("Can you play, Hotel California?", "Can you play Hotel California?")]
    [InlineData("Put on... some lofi", "Put on some lofi")]
    [InlineData("Play Stay Fly.", "Play Stay Fly.")] // nothing to take out
    [InlineData("I love this, play it again", "I love this, play it again")] // "play" isn't first
    [InlineData("Play!", "Play")] // nothing after it: no space left hanging
    [InlineData("play-doh song", "play-doh song")] // a hyphen in a word isn't a pause
    public void A_pause_after_play_isnt_a_break(string heard, string read)
        => Assert.Equal(read, ChatActionParser.PauseAfterPlay(heard));

    [Fact]
    public async Task The_model_is_asked_without_the_pause()
    {
        var chat = new FakeChat("""{"action":"none"}""");

        await Parser(chat).ParseAsync("Play, stay fly.");

        Assert.Equal("Message: Play stay fly.", chat.LastMessage);
    }

    [Fact]
    public async Task Plain_music_controls_dont_ask_the_model()
    {
        var chat = new FakeChat("""{"action":"none"}""");

        var action = await Parser(chat).ParseAsync("skip this song");

        Assert.Equal(new MusicControlAction(new MusicCommandRequest(MusicCommand.Skip)), action);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Looser_wording_goes_to_the_model_and_is_marked_as_its_pick()
    {
        // The fake answers both calls (the request check and the pick) with the same JSON.
        var chat = new FakeChat("""{"action":"music_control","command":"stop"}""");

        var action = await Parser(chat).ParseAsync("ugh i'm so done with this, kill the tunes");

        Assert.Equal(new MusicControlAction(new MusicCommandRequest(MusicCommand.Stop), FromModel: true), action);
    }

    [Fact]
    public async Task A_control_the_model_cant_place_is_unclear()
    {
        var chat = new FakeChat("""{"action":"music_control","command":"unclear"}""");

        Assert.IsType<UnclearMusicAction>(await Parser(chat).ParseAsync("make the music better somehow"));
    }

    [Theory]
    [InlineData("play mr brightside", true)]
    [InlineData("add mr brightside", false)]
    [InlineData("can you queue up mr brightside", false)]
    [InlineData("put mr brightside in the queue", false)]
    public async Task Add_only_queues_while_play_also_starts_the_music(string message, bool starts)
    {
        var chat = new FakeChat("""{"action":"play_music","query":"mr brightside"}""");

        Assert.Equal(new PlayMusicAction("mr brightside", starts), await Parser(chat).ParseAsync(message));
    }

    [Fact]
    public async Task Chat_stays_chat()
        => Assert.Null(await Parser(new FakeChat("""{"action":"none"}""")).ParseAsync("what music do you like?"));
}
