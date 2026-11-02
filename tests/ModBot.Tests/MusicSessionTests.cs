using ModBot.Modules.Music;

namespace ModBot.Tests;

public sealed class MusicSessionTests
{
    private const ulong Alice = 1, Bob = 2, Cara = 3;

    private static List<string> Arrange(IEnumerable<(ulong Picker, string Song)> songs, ulong[] participants,
        ulong? lastPicker = null)
        => FairOrder.Arrange(songs, s => s.Picker, participants, lastPicker).Select(s => s.Song).ToList();

    [Fact]
    public void Takes_one_song_from_each_person_in_turn()
    {
        var songs = new[] { (Alice, "a1"), (Alice, "a2"), (Alice, "a3"), (Bob, "b1"), (Cara, "c1"), (Cara, "c2") };

        Assert.Equal(["a1", "b1", "c1", "a2", "c2", "a3"], Arrange(songs, [Alice, Bob, Cara]));
    }

    [Fact]
    public void Next_turn_goes_to_whoever_follows_the_current_picker()
    {
        var songs = new[] { (Alice, "a2"), (Bob, "b1"), (Cara, "c1") };

        // Alice's song is playing, so Bob is up next, then Cara, then back round to Alice.
        Assert.Equal(["b1", "c1", "a2"], Arrange(songs, [Alice, Bob, Cara], lastPicker: Alice));
    }

    [Fact]
    public void Wraps_round_from_the_last_person()
    {
        var songs = new[] { (Alice, "a1"), (Bob, "b1"), (Cara, "c1") };

        Assert.Equal(["a1", "b1", "c1"], Arrange(songs, [Alice, Bob, Cara], lastPicker: Cara));
    }

    [Fact]
    public void Someone_with_nothing_waiting_is_passed_over()
    {
        var songs = new[] { (Alice, "a1"), (Alice, "a2"), (Cara, "c1") };

        Assert.Equal(["c1", "a1", "a2"], Arrange(songs, [Alice, Bob, Cara], lastPicker: Bob));
    }

    [Fact]
    public void Keeps_each_persons_own_order()
    {
        var songs = new[] { (Bob, "b1"), (Alice, "a1"), (Bob, "b2"), (Alice, "a2") };

        Assert.Equal(["a1", "b1", "a2", "b2"], Arrange(songs, [Alice, Bob]));
    }

    [Fact]
    public void Songs_from_someone_not_in_the_turns_still_play()
    {
        var songs = new[] { (Alice, "a1"), (99UL, "x1") };

        Assert.Equal(["a1", "x1"], Arrange(songs, [Alice]));
    }

    [Fact]
    public void Empty_queue_stays_empty() => Assert.Empty(Arrange([], [Alice, Bob]));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(0, 1)] // nobody counted (e.g. a moderator outside the channel): one vote
    public void A_skip_needs_more_than_half_the_listeners(int listeners, int needed)
        => Assert.Equal(needed, MusicSessionService.VotesNeeded(listeners));
}
