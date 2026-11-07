using ModBot.Services.Chat;

namespace ModBot.Tests;

public sealed class MusicCommandReaderTests
{
    [Theory]
    [InlineData("skip", MusicCommand.Skip)]
    [InlineData("skip this song", MusicCommand.Skip)]
    [InlineData("can you skip this?", MusicCommand.Skip)]
    [InlineData("hey can you please skip this song for me", MusicCommand.Skip)]
    [InlineData("vote to skip", MusicCommand.Skip)]
    [InlineData("vote skip", MusicCommand.Skip)]
    [InlineData("this song sucks, skip it", MusicCommand.Skip)]
    [InlineData("next song please", MusicCommand.Skip)]
    [InlineData("pause the music", MusicCommand.Pause)]
    [InlineData("hold on, pause it for a sec", MusicCommand.Pause)]
    [InlineData("resume", MusicCommand.Resume)]
    [InlineData("unpause", MusicCommand.Resume)]
    [InlineData("ok keep going", MusicCommand.Resume)]
    [InlineData("stop the music", MusicCommand.Stop)]
    [InlineData("stop the music and leave", MusicCommand.Stop)]
    [InlineData("leave the voice channel", MusicCommand.Stop)]
    [InlineData("turn the music off", MusicCommand.Stop)]
    [InlineData("what's playing right now?", MusicCommand.NowPlaying)]
    [InlineData("what song is this?", MusicCommand.NowPlaying)]
    [InlineData("show me the queue", MusicCommand.ShowQueue)]
    [InlineData("what's next?", MusicCommand.ShowQueue)]
    [InlineData("what's in the queue", MusicCommand.ShowQueue)]
    [InlineData("loop this song", MusicCommand.LoopSong)]
    [InlineData("put this on repeat", MusicCommand.LoopSong)]
    [InlineData("repeat the whole queue", MusicCommand.LoopQueue)]
    [InlineData("stop looping", MusicCommand.LoopOff)]
    [InlineData("loop off", MusicCommand.LoopOff)]
    [InlineData("clear the queue", MusicCommand.ClearQueue)]
    [InlineData("wipe the queue", MusicCommand.ClearQueue)]
    [InlineData("start a music session", MusicCommand.Start)]
    [InlineData("lets start the session", MusicCommand.Start)]
    [InlineData("let's do a music session", MusicCommand.Start)]
    [InlineData("open a session", MusicCommand.Start)]
    [InlineData("start the music", MusicCommand.Start)]
    [InlineData("ok everyone's ready, start playing", MusicCommand.Start)]
    [InlineData("end the session", MusicCommand.Stop)]
    [InlineData("stop the music session", MusicCommand.Stop)]
    [InlineData("I want to leave the music session", MusicCommand.RemoveMine)]
    [InlineData("take me out of the session", MusicCommand.RemoveMine)]
    [InlineData("show the session menu", MusicCommand.ShowMenu)]
    [InlineData("bring up the music panel", MusicCommand.ShowMenu)]
    [InlineData("where's the music menu?", MusicCommand.ShowMenu)]
    [InlineData("play the queue", MusicCommand.Start)]
    [InlineData("remove my songs", MusicCommand.RemoveMine)]
    [InlineData("take all my songs out of the queue", MusicCommand.RemoveMine)]
    [InlineData("clear my songs", MusicCommand.RemoveMine)] // not the whole queue
    public void Reads_plain_music_controls(string message, MusicCommand expected)
        => Assert.Equal(expected, MusicCommandReader.Read(message)?.Command);

    [Fact]
    public void Reads_which_song_to_remove()
    {
        Assert.Equal(new MusicCommandRequest(MusicCommand.Remove, 3), MusicCommandReader.Read("remove song 3 from the queue"));
        Assert.Equal(new MusicCommandRequest(MusicCommand.Remove, 4), MusicCommandReader.Read("take number 4 out of the queue"));
    }

    [Theory]
    [InlineData("remove mr brightside from the queue", "mr brightside")]
    [InlineData("can you take hotel california out of the queue?", "hotel california")]
    [InlineData("remove my song never gonna give you up from the queue", "never gonna give you up")]
    [InlineData("take the song bohemian rhapsody off the playlist", "bohemian rhapsody")]
    public void Reads_a_song_to_remove_by_name(string message, string title)
        => Assert.Equal(new MusicCommandRequest(MusicCommand.Remove, Title: title), MusicCommandReader.Read(message));

    [Theory]
    [InlineData("remove this song from the queue")] // points at a song, doesn't name one: the model's job
    [InlineData("take it out of the queue")]
    [InlineData("remove the current one from the queue")]
    [InlineData("pull the plug off the queue")]
    [InlineData("remove everything from the queue")]
    [InlineData("remove all the songs from the queue")]
    [InlineData("take them out of the queue")]
    [InlineData("I took the trash out")]
    public void Doesnt_read_a_title_that_isnt_one(string message)
        => Assert.Null(MusicCommandReader.Read(message)?.Title);

    [Theory]
    [InlineData("remove my songs from the queue", MusicCommand.RemoveMine)]
    [InlineData("remove me from the queue", MusicCommand.RemoveMine)]
    [InlineData("remove 3 from the queue", MusicCommand.Remove)]
    public void Removing_all_or_by_number_still_wins(string message, MusicCommand expected)
    {
        var read = MusicCommandReader.Read(message);
        Assert.Equal(expected, read?.Command);
        Assert.Null(read?.Title);
    }

    [Theory]
    [InlineData("play song 3 next", 3)]
    [InlineData("can you put number 4 first", 4)]
    [InlineData("move #2 to the front of my songs", 2)]
    [InlineData("bump 5 up next please", 5)]
    public void Reads_play_next_with_its_place(string message, int position)
        => Assert.Equal(new MusicCommandRequest(MusicCommand.PlayNext, position), MusicCommandReader.Read(message));

    [Theory]
    [InlineData("play 99999999999 next")]
    [InlineData("remove 99999999999999999999")]
    public void A_number_too_big_for_a_place_is_passed_over(string message)
        => Assert.Null(MusicCommandReader.Read(message)?.Position);

    [Theory]
    [InlineData("shuffle my songs")]
    [InlineData("can you mix up my picks")]
    [InlineData("randomize all my songs in the queue")]
    public void Reads_shuffling_your_own_songs(string message)
        => Assert.Equal(MusicCommand.ShuffleMine, MusicCommandReader.Read(message)?.Command);

    [Theory]
    [InlineData("play the next song", MusicCommand.Skip)] // still a skip, not a reorder
    [InlineData("next song please", MusicCommand.Skip)]
    public void Play_next_doesnt_take_over_skipping(string message, MusicCommand expected)
        => Assert.Equal(expected, MusicCommandReader.Read(message)?.Command);

    [Theory]
    [InlineData("i hate it when people skip songs")]
    [InlineData("do you like this song?")]
    [InlineData("what music do you like?")]
    [InlineData("how do music sessions work?")]
    [InlineData("lol this song is so bad")]
    [InlineData("who picked this song?")]
    [InlineData("tell me a joke")]
    [InlineData("can you recommend some good music?")]
    [InlineData("what's a good hardpoint strategy?")]
    [InlineData("stop it")]
    [InlineData("what's this?")]
    [InlineData("put on some lofi beats")] // a play request: the model's job
    [InlineData("play never gonna give you up")]
    [InlineData("I'm having a party tonight")]
    [InlineData("we should skip the next match")]
    [InlineData("remove song 0")]
    public void Leaves_other_messages_alone(string message) => Assert.Null(MusicCommandReader.Read(message));
}
