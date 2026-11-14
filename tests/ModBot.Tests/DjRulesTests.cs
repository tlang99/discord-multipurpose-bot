using ModBot.Models;
using ModBot.Modules.Music;

namespace ModBot.Tests;

public sealed class DjRulesTests
{
    private const ulong Alice = 1, Bob = 2, Dj = 99;

    private static QueuedSong Song(string title, ulong picker = Alice)
        => new() { Title = title, Track = title, Uri = $"https://youtu.be/{title}", PickerId = picker };

    [Fact]
    public void Liking_adds_newest_first_and_drops_the_oldest_past_the_limit()
    {
        var state = new MusicQueueState();
        for (var i = 1; i <= DjRules.MostLikes; i++)
        {
            Assert.Null(DjRules.Like(state, Alice, Song($"s{i}")));
        }

        var dropped = DjRules.Like(state, Alice, Song("newest"));

        Assert.Equal("s1", dropped?.Title);
        var liked = DjRules.TasteOf(state, Alice)!.Liked;
        Assert.Equal(DjRules.MostLikes, liked.Count);
        Assert.Equal("newest", liked[0].Title);
    }

    [Fact]
    public void Likes_are_each_persons_own()
    {
        var state = new MusicQueueState();
        DjRules.Like(state, Alice, Song("juicy"));

        Assert.True(DjRules.IsLiked(state, Alice, Song("juicy")));
        Assert.False(DjRules.IsLiked(state, Bob, Song("juicy")));
    }

    [Fact]
    public void Unliking_the_last_thing_forgets_the_person()
    {
        var state = new MusicQueueState();
        DjRules.Like(state, Alice, Song("juicy"));

        Assert.Equal("juicy", DjRules.Unlike(state, Alice, s => s.Title == "juicy")?.Title);
        Assert.Empty(state.Tastes);
        Assert.Null(DjRules.Unlike(state, Alice, _ => true));
    }

    [Fact]
    public void A_taste_is_trimmed_cut_and_forgotten_when_blank()
    {
        var state = new MusicQueueState();

        Assert.Equal("90s hip hop", DjRules.SetTaste(state, Alice, "  90s hip hop  "));
        Assert.Equal(DjRules.LongestTaste, DjRules.SetTaste(state, Alice, new string('a', 500)).Length);
        Assert.Equal("", DjRules.SetTaste(state, Alice, "   "));
        Assert.Empty(state.Tastes);
    }

    [Fact]
    public void Forgetting_a_taste_keeps_the_likes()
    {
        var state = new MusicQueueState();
        DjRules.SetTaste(state, Alice, "rock");
        DjRules.Like(state, Alice, Song("juicy"));

        DjRules.SetTaste(state, Alice, null);

        Assert.Single(DjRules.TasteOf(state, Alice)!.Liked);
    }

    [Fact]
    public void Only_the_listeners_tastes_count()
    {
        var state = new MusicQueueState();
        DjRules.SetTaste(state, Alice, "rock");
        DjRules.SetTaste(state, Bob, "country");

        Assert.Equal(["rock"], DjRules.TastesOf(state, new HashSet<ulong> { Alice }));
    }

    [Fact]
    public void Skipped_picks_are_newest_first_without_repeats_and_capped()
    {
        var state = new MusicQueueState();
        for (var i = 0; i < DjRules.SkipsKept + 3; i++)
        {
            DjRules.RecordSkipped(state, Song($"s{i}", Dj));
        }

        DjRules.RecordSkipped(state, Song("s5", Dj));

        Assert.Equal(DjRules.SkipsKept, state.DjSkipped.Count);
        Assert.Equal("s5", state.DjSkipped[0]);
        Assert.Single(state.DjSkipped, t => t == "s5");
    }

    [Fact]
    public void Seeds_are_the_songs_people_picked_not_dj_modes()
    {
        List<QueuedSong> history = [Song("dj1", Dj), Song("mine"), Song("dj2", Dj), Song("bobs", Bob)];

        Assert.Equal(["mine", "bobs"], DjRules.Seeds(history, Dj));
    }

    [Fact]
    public void Seeds_fall_back_to_dj_picks_when_nobody_picked_lately()
        => Assert.Equal(["dj1", "dj2"], DjRules.Seeds([Song("dj1", Dj), Song("dj2", Dj)], Dj));

    [Fact]
    public void Liked_songs_to_play_are_the_listeners_not_played_lately_and_not_twice()
    {
        var state = new MusicQueueState { History = [Song("played")] };
        DjRules.Like(state, Alice, Song("played"));
        DjRules.Like(state, Alice, Song("shared"));
        DjRules.Like(state, Bob, Song("shared"));
        DjRules.Like(state, Bob, Song("bobs"));
        var live = Song("live");
        live.IsLiveStream = true;
        DjRules.Like(state, Alice, live);
        DjRules.Like(state, 3, Song("not listening"));

        var songs = DjRules.LikedToPlay(state, new HashSet<ulong> { Alice, Bob }, new Random(1));

        Assert.Equal(["bobs", "shared"], songs.Select(s => s.Song.Title).Order());
        Assert.Equal(Alice, songs.Single(s => s.Song.Title == "shared").LikedBy);
    }

    [Fact]
    public void The_ask_shows_only_what_there_is()
    {
        var ask = DjRules.Ask(["mr brightside"], ["indie rock"], [], [], []);

        Assert.Contains("mr brightside", ask);
        Assert.Contains("\"indie rock\"", ask);
        Assert.DoesNotContain("skipped", ask);
        Assert.DoesNotContain("Already suggested", ask);
    }

    [Fact]
    public void The_same_song_is_matched_by_link_or_by_track()
    {
        Assert.True(DjRules.SameSong(Song("a"), new QueuedSong { Track = "other", Uri = "https://youtu.be/a" }));
        Assert.True(DjRules.SameSong(new QueuedSong { Track = "t" }, new QueuedSong { Track = "t", Uri = "x" }));
        Assert.False(DjRules.SameSong(Song("a"), Song("b")));
    }
}
