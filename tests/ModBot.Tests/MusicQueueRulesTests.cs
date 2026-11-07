using ModBot.Models;
using ModBot.Modules.Music;
using ModBot.Services.Storage;

namespace ModBot.Tests;

public sealed class MusicQueueRulesTests
{
    private const ulong Alice = 1, Bob = 2, Cara = 3;

    private static QueuedSong Song(ulong picker, string title) => new() { PickerId = picker, Title = title, Track = title };

    private static void Add(MusicQueueState state, ulong picker, params string[] titles)
        => Assert.Equal(QueueAddResult.Added,
            MusicQueueRules.Add(state, titles.Select(t => Song(picker, t)).ToList(), picker, 10, 200, out _));

    private static List<string> Titles(IEnumerable<QueuedSong> songs) => songs.Select(s => s.Title).ToList();

    [Fact]
    public void Songs_play_in_turns_as_they_come_off_the_queue()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2", "a3");
        Add(state, Bob, "b1");
        Add(state, Cara, "c1", "c2");

        var played = new List<string>();
        while (MusicQueueRules.TakeNext(state) is { } next)
        {
            played.Add(next.Title);
        }

        Assert.Equal(["a1", "b1", "c1", "a2", "c2", "a3"], played);
        Assert.Empty(state.Songs);
    }

    [Fact]
    public void Someone_adding_later_gets_the_next_free_turn_after_everyone_already_waiting()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2");
        Add(state, Bob, "b1", "b2");
        Assert.Equal("a1", MusicQueueRules.TakeNext(state)!.Title); // Alice's playing; Bob's up next

        Add(state, Cara, "c1");

        Assert.Equal(["b1", "c1", "a2", "b2"], Titles(MusicQueueRules.Order(state)));
    }

    [Fact]
    public void People_with_nothing_left_drop_out_of_the_turns()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1");
        Add(state, Bob, "b1", "b2");

        MusicQueueRules.TakeNext(state); // a1: Alice has nothing left, but played last
        MusicQueueRules.TakeNext(state); // b1: now Alice drops out

        Assert.Equal([Bob], state.Participants);
    }

    [Fact]
    public void Each_person_has_a_cap()
    {
        var state = new MusicQueueState();
        var result = MusicQueueRules.Add(state, Enumerable.Range(1, 12).Select(i => Song(Alice, $"a{i}")).ToList(),
            Alice, perPerson: 10, maxTotal: 200, out var added);

        Assert.Equal(QueueAddResult.Added, result);
        Assert.Equal(10, added.Count); // a playlist is cut at the cap

        Assert.Equal(QueueAddResult.PersonFull,
            MusicQueueRules.Add(state, [Song(Alice, "a13")], Alice, 10, 200, out _));
        Assert.Equal(QueueAddResult.Added, MusicQueueRules.Add(state, [Song(Bob, "b1")], Bob, 10, 200, out _));
    }

    [Fact]
    public void The_whole_queue_has_a_cap()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2");

        Assert.Equal(QueueAddResult.QueueFull, MusicQueueRules.Add(state, [Song(Bob, "b1")], Bob, 10, 2, out _));
    }

    [Fact]
    public void You_can_only_remove_your_own_songs_unless_youre_a_moderator()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1");
        Add(state, Bob, "b1");

        Assert.Equal(QueueRemoveResult.NotYours, MusicQueueRules.RemoveAt(state, 2, Alice, false, out _));
        Assert.Equal(QueueRemoveResult.Removed, MusicQueueRules.RemoveAt(state, 2, Alice, true, out var removed));
        Assert.Equal("b1", removed!.Title);
        Assert.Equal(QueueRemoveResult.NotFound, MusicQueueRules.RemoveAt(state, 5, Alice, true, out _));
    }

    [Fact]
    public void Removes_one_song_by_id_only_if_its_yours()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2");
        Add(state, Bob, "b1");
        var a2 = state.Songs.Single(s => s.Title == "a2").Id;
        var b1 = state.Songs.Single(s => s.Title == "b1").Id;

        Assert.Equal(QueueRemoveResult.NotYours, MusicQueueRules.Remove(state, b1, Alice, false, out _));
        Assert.Equal(QueueRemoveResult.Removed, MusicQueueRules.Remove(state, a2, Alice, false, out var removed));
        Assert.Equal("a2", removed!.Title);
        Assert.Equal(["a1", "b1"], Titles(state.Songs)); // only that one
        Assert.Equal(QueueRemoveResult.NotFound, MusicQueueRules.Remove(state, a2, Alice, false, out _));
    }

    [Fact]
    public void Finds_your_songs_by_part_of_the_title()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "Mr. Brightside (Official Video)", "Hotel California");
        Add(state, Bob, "Mr Brightside - Live");

        Assert.Equal(["Mr. Brightside (Official Video)"], Titles(MusicQueueRules.FindByTitle(state, "mr brightside", Alice, false)));
        Assert.Equal(2, MusicQueueRules.FindByTitle(state, "brightside", Alice, true).Count); // a moderator: anyone's
        Assert.Empty(MusicQueueRules.FindByTitle(state, "!!", Alice, false));
    }

    [Fact]
    public void Remove_all_takes_only_that_persons_songs()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2");
        Add(state, Bob, "b1");

        Assert.Equal(2, MusicQueueRules.RemoveAllFor(state, Alice));
        Assert.Equal(["b1"], Titles(state.Songs));
        Assert.Equal([Bob], state.Participants);
    }

    [Fact]
    public void Looping_the_queue_puts_a_finished_song_back_for_its_picker()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1");
        Add(state, Bob, "b1");

        var played = MusicQueueRules.TakeNext(state)!;
        MusicQueueRules.Requeue(state, played);

        Assert.Equal(["b1", "a1"], Titles(MusicQueueRules.Order(state)));
        Assert.NotEqual(played.Id, state.Songs.Single(s => s.Title == "a1").Id); // a fresh entry, not the same one
    }

    [Fact]
    public async Task The_queue_is_saved_and_comes_back()
    {
        var directory = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "music-queue.json");
            await new JsonMusicQueueStore(path).MutateAsync(5, state =>
            {
                MusicQueueRules.Add(state, [Song(Alice, "a1")], Alice, 10, 200, out _);
                return true;
            });

            var reloaded = await new JsonMusicQueueStore(path).GetAsync(5);

            Assert.Equal(["a1"], Titles(reloaded.Songs));
            Assert.Equal([Alice], reloaded.Participants);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
