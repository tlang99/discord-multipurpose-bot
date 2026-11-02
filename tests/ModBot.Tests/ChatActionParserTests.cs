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

    [Fact]
    public async Task Chat_stays_chat()
        => Assert.Null(await Parser(new FakeChat("""{"action":"none"}""")).ParseAsync("what music do you like?"));
}
