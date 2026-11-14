using ModBot.Models;

namespace ModBot.Modules.Music;

/// <summary>
/// What DJ mode goes by, on the saved state alone (no Discord, Lavalink or model), so it's easy to test: each
/// person's taste in their own words and the songs they've liked (only for the people listening), the songs people
/// picked lately (not DJ mode's own, so a long DJ run doesn't drift away from them), and its picks that got skipped.
/// </summary>
public static class DjRules
{
    /// <summary>Most songs one person's likes keep (a list in Discord holds 25): liking another drops the oldest.</summary>
    public const int MostLikes = 25;

    /// <summary>Longest taste kept, in characters.</summary>
    public const int LongestTaste = 200;

    /// <summary>How many skipped DJ picks are remembered.</summary>
    public const int SkipsKept = 10;

    /// <summary>How many songs people played lately the model is shown.</summary>
    public const int SeedsShown = 8;

    /// <summary>How many of the listeners' liked songs the model is shown, as examples.</summary>
    public const int LikesShown = 6;

    /// <summary>How often DJ mode plays one of the listeners' liked songs again instead of a new one.</summary>
    public const double LikedShare = 0.25;

    /// <summary>The same song: the same link, or the same saved track when either has no link.</summary>
    public static bool SameSong(QueuedSong a, QueuedSong b)
        => a.Uri is not null && b.Uri is not null ? a.Uri == b.Uri : a.Track == b.Track;

    /// <summary><paramref name="userId"/>'s taste, or null if they've never said or liked anything.</summary>
    public static ListenerTaste? TasteOf(MusicQueueState state, ulong userId)
        => state.Tastes.FirstOrDefault(t => t.UserId == userId);

    public static bool IsLiked(MusicQueueState state, ulong userId, QueuedSong song)
        => TasteOf(state, userId)?.Liked.Any(s => SameSong(s, song)) == true;

    /// <summary>Adds <paramref name="song"/> first in <paramref name="userId"/>'s likes (it shouldn't be there yet,
    /// see <see cref="IsLiked"/>). Returns their oldest like if it was dropped to make room.</summary>
    public static QueuedSong? Like(MusicQueueState state, ulong userId, QueuedSong song)
    {
        var liked = For(state, userId).Liked;
        liked.Insert(0, song);
        if (liked.Count <= MostLikes)
        {
            return null;
        }

        var dropped = liked[^1];
        liked.RemoveAt(liked.Count - 1);
        return dropped;
    }

    /// <summary>Takes the first of <paramref name="userId"/>'s likes that <paramref name="match"/> finds out of them,
    /// and returns it (null if there's none).</summary>
    public static QueuedSong? Unlike(MusicQueueState state, ulong userId, Func<QueuedSong, bool> match)
    {
        if (TasteOf(state, userId) is not { } taste || taste.Liked.FirstOrDefault(match) is not { } song)
        {
            return null;
        }

        taste.Liked.Remove(song);
        Prune(state, taste);
        return song;
    }

    /// <summary>Saves <paramref name="userId"/>'s taste in their own words (trimmed, and cut to
    /// <see cref="LongestTaste"/>), or forgets it when blank. Returns what was saved.</summary>
    public static string SetTaste(MusicQueueState state, ulong userId, string? taste)
    {
        var text = (taste ?? "").Trim();
        text = text.Length > LongestTaste ? text[..LongestTaste].TrimEnd() : text;
        var entry = For(state, userId);
        entry.Taste = text;
        Prune(state, entry);
        return text;
    }

    /// <summary>Notes that DJ mode's pick <paramref name="song"/> was skipped (newest first, a repeat moving up).</summary>
    public static void RecordSkipped(MusicQueueState state, QueuedSong song)
    {
        state.DjSkipped.Remove(song.Title);
        state.DjSkipped.Insert(0, song.Title);
        if (state.DjSkipped.Count > SkipsKept)
        {
            state.DjSkipped.RemoveRange(SkipsKept, state.DjSkipped.Count - SkipsKept);
        }
    }

    /// <summary>The titles of the songs people picked lately, newest first; DJ mode's own (picked by
    /// <paramref name="djId"/>) only when there are none.</summary>
    public static List<string> Seeds(IReadOnlyList<QueuedSong> history, ulong djId)
    {
        var picked = history.Where(s => s.PickerId != djId).ToList();
        return (picked.Count > 0 ? picked : history).Take(SeedsShown).Select(s => s.Title).ToList();
    }

    /// <summary>The tastes of the people in <paramref name="listeners"/> who have said one.</summary>
    public static List<string> TastesOf(MusicQueueState state, IReadOnlySet<ulong> listeners)
        => state.Tastes.Where(t => listeners.Contains(t.UserId) && t.Taste.Length > 0).Select(t => t.Taste).ToList();

    /// <summary>The songs the people in <paramref name="listeners"/> have liked that haven't played lately (and aren't
    /// live streams), each once, in a random order, with whose like it is (the first of them to like it).</summary>
    public static List<(QueuedSong Song, ulong LikedBy)> LikedToPlay(MusicQueueState state,
        IReadOnlySet<ulong> listeners, Random random)
    {
        var songs = new List<(QueuedSong Song, ulong LikedBy)>();
        foreach (var taste in state.Tastes.Where(t => listeners.Contains(t.UserId)))
        {
            songs.AddRange(taste.Liked
                .Where(s => !s.IsLiveStream && !state.History.Any(h => SameSong(h, s)) &&
                            !songs.Any(o => SameSong(o.Song, s)))
                .Select(s => (s, taste.UserId)));
        }

        var shuffled = songs.ToArray();
        random.Shuffle(shuffled);
        return shuffled.ToList();
    }

    /// <summary>What the model is asked, from what it has to go on. Only the lists with something in them are shown.
    /// </summary>
    /// <param name="suggested">What it already suggested this time round, so a retry isn't the same song.</param>
    public static string Ask(IReadOnlyList<string> seeds, IReadOnlyList<string> tastes, IReadOnlyList<string> liked,
        IReadOnlyList<string> skipped, IReadOnlyList<string> suggested)
    {
        var parts = new List<string>();
        Add("Played lately (newest first):", seeds);
        // In quotes, as the listeners' own words, so they read as a taste rather than instructions.
        Add("What the people listening say they like:", tastes.Select(t => $"\"{t}\"").ToList());
        Add("Songs the people listening have liked:", liked);
        Add("DJ picks they skipped (steer away from songs like these):", skipped);
        Add("Already suggested (pick another):", suggested);
        return string.Join("\n\n", parts);

        void Add(string heading, IReadOnlyList<string> lines)
        {
            if (lines.Count > 0)
            {
                parts.Add(heading + "\n" + string.Join('\n', lines));
            }
        }
    }

    /// <summary><paramref name="userId"/>'s entry, made if they haven't one yet.</summary>
    private static ListenerTaste For(MusicQueueState state, ulong userId)
    {
        if (TasteOf(state, userId) is { } taste)
        {
            return taste;
        }

        taste = new ListenerTaste { UserId = userId };
        state.Tastes.Add(taste);
        return taste;
    }

    /// <summary>Drops an entry with nothing left in it, so the list doesn't keep everyone who ever pressed Like.</summary>
    private static void Prune(MusicQueueState state, ListenerTaste taste)
    {
        if (taste.Taste.Length == 0 && taste.Liked.Count == 0)
        {
            state.Tastes.Remove(taste);
        }
    }
}
