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

    [Theory]
    [InlineData("previous", MusicCommand.Previous)]
    [InlineData("play_again", MusicCommand.PlayAgain)]
    [InlineData("restart", MusicCommand.Restart)]
    [InlineData("volume_down", MusicCommand.VolumeDown)]
    [InlineData("volume_up", MusicCommand.VolumeUp)]
    [InlineData("shuffle_mine", MusicCommand.ShuffleMine)]
    [InlineData("show_history", MusicCommand.ShowHistory)]
    public async Task The_model_knows_the_newer_music_controls_too(string picked, MusicCommand expected)
    {
        var chat = new FakeChat($$"""{"action":"music_control","command":"{{picked}}"}""");

        var action = await Parser(chat).ParseAsync("could you sort of bring back that tune from before");

        Assert.Equal(new MusicControlAction(new MusicCommandRequest(expected), FromModel: true), action);
    }

    [Fact]
    public async Task The_model_can_save_a_taste_in_the_persons_words()
    {
        var chat = new FakeChat("""{"action":"music_control","command":"set_taste","taste":"chill lofi"}""");

        var action = await Parser(chat).ParseAsync("honestly i'm mostly a chill lofi person when i game");

        Assert.Equal(new MusicControlAction(new MusicCommandRequest(MusicCommand.SetTaste, Taste: "chill lofi"),
            FromModel: true), action);
    }

    [Fact]
    public async Task A_taste_with_nothing_in_it_is_unclear()
    {
        var chat = new FakeChat("""{"action":"music_control","command":"set_taste","taste":" "}""");

        Assert.IsType<UnclearMusicAction>(await Parser(chat).ParseAsync("remember my kind of stuff"));
    }

    [Fact]
    public async Task Looser_wording_goes_to_the_model_and_is_marked_as_its_pick()
    {
        // The fake answers both calls (the request check and the pick) with the same JSON.
        var chat = new FakeChat("""{"action":"music_control","command":"stop"}""");

        var action = await Parser(chat).ParseAsync("ugh i'm so done with this, make the noise go away");

        Assert.Equal(new MusicControlAction(new MusicCommandRequest(MusicCommand.Stop), FromModel: true), action);
    }

    [Fact]
    public async Task A_control_the_model_cant_place_is_unclear()
    {
        var chat = new FakeChat("""{"action":"music_control","command":"unclear"}""");

        Assert.IsType<UnclearMusicAction>(await Parser(chat).ParseAsync("make the music better somehow"));
    }

    [Theory]
    [InlineData("play mr brightside")]
    [InlineData("add mr brightside")]
    [InlineData("can you queue up mr brightside")]
    [InlineData("put mr brightside in the queue")]
    public async Task Play_and_add_are_the_same_song_request(string message)
    {
        var chat = new FakeChat("""{"action":"play_music","songs":["mr brightside"]}""");

        Assert.Equal(["mr brightside"], Assert.IsType<PlayMusicAction>(await Parser(chat).ParseAsync(message)).Queries);
    }

    [Fact]
    public async Task Several_songs_in_one_message_are_each_their_own_search()
    {
        var chat = new FakeChat("""{"action":"play_music","songs":["stay fly three 6 mafia"," ","hello, goodbye beatles"]}""");

        // Blanks are dropped, and a comma inside one song's search doesn't split it.
        Assert.Equal(["stay fly three 6 mafia", "hello, goodbye beatles"],
            Assert.IsType<PlayMusicAction>(await Parser(chat).ParseAsync("play stay fly and hello goodbye")).Queries);
    }

    [Fact]
    public async Task Every_youtube_link_in_a_message_is_queued()
    {
        var chat = new FakeChat("""{"action":"play_music","songs":["ignored"]}""");

        Assert.Equal(["https://youtu.be/aaa", "https://www.youtube.com/watch?v=bbb"],
            Assert.IsType<PlayMusicAction>(await Parser(chat)
                .ParseAsync("play https://youtu.be/aaa and https://www.youtube.com/watch?v=bbb")).Queries);
    }

    [Fact]
    public async Task Chat_stays_chat()
        => Assert.Null(await Parser(new FakeChat("""{"action":"none"}""")).ParseAsync("what music do you like?"));
}
