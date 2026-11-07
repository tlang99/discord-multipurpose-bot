using ModBot.Common;
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

    private static QueuedSong Timed(ulong picker, string title, int minutes)
        => new() { PickerId = picker, Title = title, Track = title, Duration = TimeSpan.FromMinutes(minutes) };

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

    [Theory]
    // 190 waiting (none Alice's) and a cap of 10 each: Alice's own room (10) runs out before the queue's (10) - a
    // tie goes to the person - so a 15-song playlist that leaves the queue exactly full blames her limit, not the queue.
    [InlineData(190, 0, QueueAddResult.PersonFull)]
    // 195 waiting: the queue has room for 5, Alice for 10, so the queue is what stops her.
    [InlineData(195, 0, QueueAddResult.QueueFull)]
    // Alice already has 8 of 190: her room (2) is tighter than the queue's (10).
    [InlineData(190, 8, QueueAddResult.PersonFull)]
    public void The_tighter_limit_is_the_one_named(int waiting, int alices, QueueAddResult expected)
    {
        var state = new MusicQueueState();
        state.Songs.AddRange(Enumerable.Range(0, waiting).Select(i => Song(i < alices ? Alice : Bob, $"s{i}")));

        Assert.Equal(expected, MusicQueueRules.TighterLimit(state, Alice, perPerson: 10, maxTotal: 200));
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
        Assert.Empty(MusicQueueRules.FindByTitle(state, "a", Alice, false)); // too short to go on
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

    [Fact]
    public void The_wait_before_a_song_is_the_songs_ahead_of_it_in_turn_order()
    {
        var state = new MusicQueueState();
        MusicQueueRules.Add(state, [Timed(Alice, "a1", 3), Timed(Alice, "a2", 4)], Alice, 10, 200, out _);
        MusicQueueRules.Add(state, [Timed(Bob, "b1", 5)], Bob, 10, 200, out var bob);
        var a2 = state.Songs.Single(s => s.Title == "a2");

        Assert.Equal(TimeSpan.FromMinutes(3), MusicQueueRules.WaitBefore(state, bob[0].Id)); // a1, then b1
        Assert.Equal(TimeSpan.FromMinutes(8), MusicQueueRules.WaitBefore(state, a2.Id)); // a1, b1, then a2
        Assert.Null(MusicQueueRules.WaitBefore(state, Guid.NewGuid()));
    }

    [Fact]
    public void A_live_stream_ahead_makes_the_wait_unknown()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1");
        MusicQueueRules.Add(state, [Song(Bob, "b1")], Bob, 10, 200, out var bob);
        state.Songs[0].IsLiveStream = true;

        Assert.Null(MusicQueueRules.WaitBefore(state, bob[0].Id));
    }

    [Theory]
    [InlineData(30, "under a minute")]
    [InlineData(60, "about 1 min")]
    [InlineData(61, "about 2 min")]
    [InlineData(725, "about 13 min")]
    public void Waits_read_as_rough_minutes(int seconds, string expected)
        => Assert.Equal(expected, MusicFormatting.RoughWait(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Play_next_moves_your_song_up_without_moving_anyone_elses()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2", "a3");
        Add(state, Bob, "b1", "b2");

        // Play order: a1, b1, a2, b2, a3. Alice's a3 is #5.
        Assert.Equal(QueueRemoveResult.Removed, MusicQueueRules.PlayNextAt(state, 5, Alice, out var moved));
        Assert.Equal("a3", moved!.Title);
        Assert.Equal(["a3", "b1", "a1", "b2", "a2"], Titles(MusicQueueRules.Order(state)));
    }

    [Fact]
    public void Play_next_is_only_for_your_own_songs()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1");
        Add(state, Bob, "b1", "b2");

        Assert.Equal(QueueRemoveResult.NotYours, MusicQueueRules.PlayNextAt(state, 3, Alice, out _));
        Assert.Equal(QueueRemoveResult.NotFound, MusicQueueRules.PlayNextAt(state, 9, Alice, out _));
        Assert.Equal(["a1", "b1", "b2"], Titles(MusicQueueRules.Order(state)));
    }

    [Fact]
    public void Shuffling_your_songs_leaves_everyone_elses_in_place()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a1", "a2", "a3", "a4", "a5");
        Add(state, Bob, "b1", "b2");

        Assert.Equal(5, MusicQueueRules.ShuffleMine(state, Alice, new Random(7)));

        var order = Titles(MusicQueueRules.Order(state));
        Assert.Equal("b1", order[1]); // Bob's turns don't move
        Assert.Equal("b2", order[3]);
        Assert.Equal(["a1", "a2", "a3", "a4", "a5"], order.Where(t => t.StartsWith('a')).Order().ToList());
        Assert.Equal(0, MusicQueueRules.ShuffleMine(state, Cara, Random.Shared));
    }

    [Fact]
    public void History_keeps_the_newest_first_without_repeats_and_only_so_many()
    {
        var state = new MusicQueueState();
        for (var i = 1; i <= MusicQueueRules.HistoryLength + 5; i++)
        {
            MusicQueueRules.RecordPlayed(state, Song(Alice, $"s{i}"));
        }

        MusicQueueRules.RecordPlayed(state, Song(Bob, "s10")); // played again: moves up, isn't listed twice

        Assert.Equal(MusicQueueRules.HistoryLength, state.History.Count);
        Assert.Equal("s10", state.History[0].Title);
        Assert.Equal($"s{MusicQueueRules.HistoryLength + 5}", state.History[1].Title);
        Assert.Single(state.History, s => s.Title == "s10");
    }

    [Fact]
    public void A_song_played_again_keeps_its_place_id_in_the_history()
    {
        var state = new MusicQueueState();
        var song = Song(Alice, "mr brightside");
        MusicQueueRules.RecordPlayed(state, song);
        var listed = state.History[0].Id; // what an open History list would pick by
        MusicQueueRules.RecordPlayed(state, Song(Bob, "other"));

        MusicQueueRules.RecordPlayed(state, Song(Bob, "mr brightside"));

        Assert.Equal(listed, state.History[0].Id);
    }

    [Fact]
    public void A_song_cut_off_by_going_back_plays_on_its_pickers_next_turn()
    {
        var state = new MusicQueueState();
        Add(state, Alice, "a2");
        Add(state, Bob, "b1");

        MusicQueueRules.PutBackFirst(state, Song(Alice, "a1"));

        Assert.Equal(["a1", "b1", "a2"], Titles(MusicQueueRules.Order(state)));
    }

    [Fact]
    public void Going_back_again_goes_further_back_rather_than_bouncing()
    {
        var state = new MusicQueueState();
        QueuedSong a = Song(Alice, "a"), b = Song(Alice, "b"), c = Song(Alice, "c");
        foreach (var song in new[] { a, b, c })
        {
            MusicQueueRules.RecordPlayed(state, song); // as each one starts: c is playing now
        }

        // Back from c plays b (and b starting is noted, as the player would); back again plays a, not c.
        var first = MusicQueueRules.PreviousOf(state, c)!;
        MusicQueueRules.GoBack(state, c);
        MusicQueueRules.RecordPlayed(state, first);
        var second = MusicQueueRules.PreviousOf(state, first)!;

        Assert.Equal(("b", "a"), (first.Title, second.Title));
        Assert.Equal(["c"], Titles(MusicQueueRules.Order(state))); // c waits to play again, not lost
    }

    [Fact]
    public void There_is_nothing_to_go_back_to_before_the_first_song()
    {
        var state = new MusicQueueState();
        var only = Song(Alice, "a");
        MusicQueueRules.RecordPlayed(state, only);

        Assert.Null(MusicQueueRules.PreviousOf(state, only));
    }

    [Fact]
    public void A_song_played_again_is_a_new_copy_for_whoever_asked()
    {
        var original = Song(Alice, "mr brightside");
        var again = MusicQueueRules.Copy(original, Bob, "Bob");

        Assert.NotEqual(original.Id, again.Id);
        Assert.Equal((Bob, "Bob", "mr brightside", "mr brightside"),
            (again.PickerId, again.PickerName, again.Title, again.Track));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(70, 70)]
    [InlineData(999, MusicQueueRules.MaxVolume)]
    public void Volume_stays_between_silent_and_the_most(int asked, int set)
        => Assert.Equal(set, MusicQueueRules.ClampVolume(asked));

    [Theory]
    [InlineData("1:30", 90)]
    [InlineData("90", 90)]
    [InlineData("0", 0)]
    [InlineData("1:02:03", 3723)]
    [InlineData(" 2:05 ", 125)]
    public void Times_in_a_song_read_as_people_write_them(string text, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), TextTools.ParseTime(text));

    [Theory]
    [InlineData("1:75")]
    [InlineData("abc")]
    [InlineData("1::30")]
    [InlineData("")]
    [InlineData("99999")]
    [InlineData("1:2:3:4")]
    public void Things_that_arent_times_arent(string text) => Assert.Null(TextTools.ParseTime(text));

    [Theory]
    [InlineData(1, false, false, true)] // alone, or with one other: no vote
    [InlineData(2, false, false, true)]
    [InlineData(3, false, false, false)] // 3 in the channel (deafened or not): a vote
    [InlineData(6, false, false, false)]
    [InlineData(6, true, false, true)] // a moderator
    [InlineData(6, false, true, true)] // whoever started the music
    public void Stopping_takes_a_vote_with_three_or_more_in_the_channel(int inChannel, bool moderator, bool starter,
        bool atOnce)
        => Assert.Equal(atOnce, MusicQueueService.StopsAtOnce(inChannel, moderator, starter));

    [Theory]
    [InlineData(false, LoopMode.Off)]
    [InlineData(true, LoopMode.Queue)]
    public void Looping_the_queue_shows_without_a_player(bool loopQueue, LoopMode expected)
        => Assert.Equal(expected, MusicQueueService.LoopOf(new MusicQueueState { LoopQueue = loopQueue }, null));
}
