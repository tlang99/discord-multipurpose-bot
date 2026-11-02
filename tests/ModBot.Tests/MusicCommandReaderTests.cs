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
    [InlineData("shuffle the queue", MusicCommand.Shuffle)]
    [InlineData("mix up the songs", MusicCommand.Shuffle)]
    [InlineData("loop this song", MusicCommand.LoopSong)]
    [InlineData("put this on repeat", MusicCommand.LoopSong)]
    [InlineData("repeat the whole queue", MusicCommand.LoopQueue)]
    [InlineData("stop looping", MusicCommand.LoopOff)]
    [InlineData("loop off", MusicCommand.LoopOff)]
    [InlineData("clear the queue", MusicCommand.ClearQueue)]
    [InlineData("wipe the queue", MusicCommand.ClearQueue)]
    [InlineData("start a music session", MusicCommand.StartSession)]
    [InlineData("lets start the session", MusicCommand.StartSession)]
    [InlineData("let's do a music session", MusicCommand.StartSession)]
    [InlineData("open a session", MusicCommand.StartSession)]
    [InlineData("start the music", MusicCommand.BeginSession)]
    [InlineData("ok everyone's ready, start playing", MusicCommand.BeginSession)]
    [InlineData("end the session", MusicCommand.EndSession)]
    [InlineData("stop the music session", MusicCommand.EndSession)]
    [InlineData("I want to leave the music session", MusicCommand.LeaveSession)]
    [InlineData("take me out of the session", MusicCommand.LeaveSession)]
    [InlineData("show the session menu", MusicCommand.ShowSession)]
    [InlineData("bring up the music panel", MusicCommand.ShowSession)]
    public void Reads_plain_music_controls(string message, MusicCommand expected)
        => Assert.Equal(expected, MusicCommandReader.Read(message)?.Command);

    [Fact]
    public void Reads_which_song_to_remove()
    {
        Assert.Equal(new MusicCommandRequest(MusicCommand.Remove, 3), MusicCommandReader.Read("remove song 3 from the queue"));
        Assert.Equal(new MusicCommandRequest(MusicCommand.Remove, 4), MusicCommandReader.Read("take number 4 out of the queue"));
    }

    [Fact]
    public void Reads_where_to_move_a_song()
        => Assert.Equal(new MusicCommandRequest(MusicCommand.Move, 5, 1), MusicCommandReader.Read("move song 5 to 1"));

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
