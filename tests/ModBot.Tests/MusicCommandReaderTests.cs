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
    [InlineData("turn it down", MusicCommand.VolumeDown)]
    [InlineData("can you make it quieter please", MusicCommand.VolumeDown)]
    [InlineData("its too loud", MusicCommand.VolumeDown)]
    [InlineData("turn the music up a bit", MusicCommand.VolumeUp)]
    [InlineData("louder", MusicCommand.VolumeUp)]
    [InlineData("restart this song", MusicCommand.Restart)]
    [InlineData("start the song over", MusicCommand.Restart)]
    [InlineData("go back to the start", MusicCommand.Restart)]
    [InlineData("go back", MusicCommand.Previous)]
    [InlineData("stop the song", MusicCommand.Stop)]
    [InlineData("stop playing this", MusicCommand.Stop)]
    [InlineData("stop playing", MusicCommand.Stop)]
    [InlineData("turn it off", MusicCommand.Stop)]
    [InlineData("kill the music", MusicCommand.Stop)]
    [InlineData("play the previous song", MusicCommand.Previous)]
    [InlineData("replay the last song", MusicCommand.Previous)] // asked by voice, and missed, in v0.5.4
    [InlineData("can you replay the previous song", MusicCommand.Previous)]
    [InlineData("play the last song again", MusicCommand.Previous)]
    [InlineData("play the song before this one", MusicCommand.Previous)]
    [InlineData("replay this song", MusicCommand.Restart)] // this one, over again: still a restart
    [InlineData("play the other song", null)] // a different song, not the one before: left to the model
    [InlineData("play that again", MusicCommand.PlayAgain)]
    [InlineData("queue the last song again", MusicCommand.PlayAgain)]
    [InlineData("what was that song?", MusicCommand.ShowHistory)]
    [InlineData("show me the recently played songs", MusicCommand.ShowHistory)]
    [InlineData("play again", MusicCommand.Resume)] // still unpausing, as before
    [InlineData("turn the music off", MusicCommand.Stop)] // still a stop, not a volume
    public void Reads_volume_going_back_and_history(string message, MusicCommand? expected)
        => Assert.Equal(expected, MusicCommandReader.Read(message)?.Command);

    [Theory]
    [InlineData("volume 50", 50)]
    [InlineData("set the volume to 70%", 70)]
    [InlineData("volume 0", 0)]
    public void Reads_a_volume_to_set(string message, int volume)
        => Assert.Equal(new MusicCommandRequest(MusicCommand.SetVolume, Volume: volume),
            MusicCommandReader.Read(message));

    [Theory]
    [InlineData("skip to 1:30", 90)]
    [InlineData("hey can you jump ahead to 2:05 please", 125)]
    [InlineData("play it from 45", 45)]
    [InlineData("seek to 1:02:03.", 3723)]
    [InlineData("skip to 1:30, thanks", 90)]
    public void Reads_a_jump_in_the_song_with_its_time(string message, int seconds)
        => Assert.Equal(new MusicCommandRequest(MusicCommand.Seek, At: TimeSpan.FromSeconds(seconds)),
            MusicCommandReader.Read(message));

    [Theory]
    [InlineData("seek to 90", 90)]
    [InlineData("skip ahead to 0:45", 45)]
    public void Plain_seconds_only_jump_with_seek_or_from(string message, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), MusicCommandReader.Read(message)?.At);

    [Theory]
    [InlineData("skip 2")] // no "to": not a jump
    [InlineData("skip to 2")] // could be song 2: a jump needs minutes and seconds
    [InlineData("go to 5")]
    [InlineData("volume 99999999999")]
    public void Not_a_jump_or_a_volume(string message)
        => Assert.False(MusicCommandReader.Read(message) is { Command: MusicCommand.Seek or MusicCommand.SetVolume });

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
