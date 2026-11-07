using ModBot.Services.Voice;

namespace ModBot.Tests;

/// <summary>Turning the music down while the bot talks over it (with music on its own account), and back up.</summary>
public class MusicDuckingTests
{
    [Fact]
    public void Only_the_first_answer_turns_it_down_and_only_the_last_back_up()
    {
        var ducking = new MusicDucking();

        Assert.True(ducking.Begin());   // first answer: turn it down
        Assert.False(ducking.Begin());  // a second one overlapping: already down
        Assert.False(ducking.End());    // one of them done: the other's still talking
        Assert.True(ducking.End());     // the last one done: back up
        Assert.True(ducking.Begin());   // and the next answer starts over
    }

    [Theory]
    [InlineData(1f, 25, 0.25f)]
    [InlineData(0.8f, 50, 0.4f)]
    [InlineData(1f, 100, 1f)]   // 100: no dip
    [InlineData(1f, 0, 0f)]     // 0: silent while it talks
    [InlineData(1f, 150, 1f)]   // out of range: held to 0-100
    [InlineData(1f, -10, 0f)]
    public void Lowered_is_a_share_of_the_volume(float volume, int percent, float expected)
        => Assert.Equal(expected, MusicDucking.Lowered(volume, percent), 3);

    [Fact]
    public void Back_up_to_what_it_was_once()
    {
        var ducking = new MusicDucking();
        ducking.TurnedDown(before: 0.8f, to: 0.2f);

        Assert.Equal(0.8f, ducking.ToRestore(now: 0.2f));
        Assert.Null(ducking.ToRestore(now: 0.2f)); // once per turn-down
    }

    [Fact]
    public void Left_alone_if_someone_changed_the_volume_meanwhile()
    {
        var ducking = new MusicDucking();
        ducking.TurnedDown(before: 1f, to: 0.25f);

        Assert.Null(ducking.ToRestore(now: 0.6f)); // set to 60% while it talked: that's what it stays at
    }

    [Fact]
    public void Nothing_to_restore_if_it_was_never_turned_down()
        => Assert.Null(new MusicDucking().ToRestore(now: 1f));
}
