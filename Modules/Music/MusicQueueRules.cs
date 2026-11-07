using ModBot.Common;
using ModBot.Models;

namespace ModBot.Modules.Music;

public enum QueueAddResult
{
    Added,

    /// <summary>The person already has the most songs waiting that one person can.</summary>
    PersonFull,

    /// <summary>The whole queue is at its limit.</summary>
    QueueFull,
}

public enum QueueRemoveResult
{
    Removed,
    NotFound,

    /// <summary>Someone else's song, and the person asking isn't a moderator.</summary>
    NotYours,
}

/// <summary>
/// The standing queue's rules, on the saved state alone (no Discord or Lavalink), so they're easy to test: songs play
/// in turns (see <see cref="FairOrder"/>), each person has a cap, and only your own songs are yours to remove.
/// </summary>
public static class MusicQueueRules
{
    /// <summary>The waiting songs in the order they'll play.</summary>
    public static List<QueuedSong> Order(MusicQueueState state)
        => FairOrder.Arrange(state.Songs, s => s.PickerId, state.Participants, state.LastPickerId);

    /// <summary>Adds as many of <paramref name="songs"/> as the limits allow (all of them, or none if the person or
    /// queue is full).</summary>
    public static QueueAddResult Add(MusicQueueState state, IReadOnlyList<QueuedSong> songs, ulong pickerId,
        int perPerson, int maxTotal, out List<QueuedSong> added)
    {
        var room = Math.Min(perPerson - state.Songs.Count(s => s.PickerId == pickerId), maxTotal - state.Songs.Count);
        if (room <= 0)
        {
            added = [];
            return TighterLimit(state, pickerId, perPerson, maxTotal);
        }

        added = songs.Take(room).ToList();
        state.Songs.AddRange(added);
        if (!state.Participants.Contains(pickerId))
        {
            state.Participants.Add(pickerId);
        }

        return QueueAddResult.Added;
    }

    /// <summary>Which limit an add for <paramref name="pickerId"/> runs into first, checked before the add: the
    /// person's own (PersonFull) or the whole queue's (QueueFull). The person's own when both have the same room.</summary>
    public static QueueAddResult TighterLimit(MusicQueueState state, ulong pickerId, int perPerson, int maxTotal)
        => perPerson - state.Songs.Count(s => s.PickerId == pickerId) <= maxTotal - state.Songs.Count
            ? QueueAddResult.PersonFull
            : QueueAddResult.QueueFull;

    /// <summary>Takes the next song off the queue (it's someone else's turn after it), or null if none are waiting.</summary>
    public static QueuedSong? TakeNext(MusicQueueState state)
    {
        if (Order(state).FirstOrDefault() is not { } next)
        {
            return null;
        }

        state.Songs.Remove(state.Songs.First(s => s.Id == next.Id));
        state.LastPickerId = next.PickerId;
        Prune(state);
        return next;
    }

    /// <summary>Puts a song that just finished back at the end of its picker's songs (for looping the queue).</summary>
    public static void Requeue(MusicQueueState state, QueuedSong song)
    {
        state.Songs.Add(new QueuedSong
        {
            Track = song.Track,
            Title = song.Title,
            Uri = song.Uri,
            Duration = song.Duration,
            IsLiveStream = song.IsLiveStream,
            PickerId = song.PickerId,
            PickerName = song.PickerName,
        });
        if (!state.Participants.Contains(song.PickerId))
        {
            state.Participants.Add(song.PickerId);
        }
    }

    /// <summary>Removes the song at <paramref name="position"/> in play order (1 = next).</summary>
    public static QueueRemoveResult RemoveAt(MusicQueueState state, int position, ulong userId, bool isModerator,
        out QueuedSong? removed)
    {
        var result = FindAt(state, position, userId, isModerator, out removed);
        if (removed is { } song)
        {
            state.Songs.RemoveAll(s => s.Id == song.Id);
            Prune(state);
        }

        return result;
    }

    /// <summary>Moves <paramref name="userId"/>'s song at <paramref name="position"/> in play order to the front of
    /// their own songs, so it's the one they play on their next turn. Only their own songs move, so nobody else's
    /// turn comes any later. Removed (here: moved) unless it's not there or not theirs.</summary>
    public static QueueRemoveResult PlayNextAt(MusicQueueState state, int position, ulong userId, out QueuedSong? song)
    {
        var result = FindAt(state, position, userId, isModerator: false, out song);
        if (result == QueueRemoveResult.Removed)
        {
            var moving = song!;
            state.Songs.Remove(moving);
            var firstOfTheirs = state.Songs.FindIndex(s => s.PickerId == userId);
            state.Songs.Insert(firstOfTheirs < 0 ? state.Songs.Count : firstOfTheirs, moving);
        }

        return result;
    }

    /// <summary>Shuffles <paramref name="userId"/>'s own waiting songs among themselves (everyone else's stay put);
    /// returns how many they have.</summary>
    public static int ShuffleMine(MusicQueueState state, ulong userId, Random random)
    {
        var slots = Enumerable.Range(0, state.Songs.Count).Where(i => state.Songs[i].PickerId == userId).ToList();
        var mine = slots.Select(i => state.Songs[i]).ToArray();
        random.Shuffle(mine);
        for (var i = 0; i < slots.Count; i++)
        {
            state.Songs[slots[i]] = mine[i];
        }

        return mine.Length;
    }

    /// <summary>The song at <paramref name="position"/> in play order (1 = next), if <paramref name="userId"/> may
    /// change it: theirs, or anyone's for a moderator. Removed means it was found and may be changed.</summary>
    private static QueueRemoveResult FindAt(MusicQueueState state, int position, ulong userId, bool isModerator,
        out QueuedSong? song)
    {
        song = null;
        var order = Order(state);
        if (position < 1 || position > order.Count)
        {
            return QueueRemoveResult.NotFound;
        }

        var found = order[position - 1];
        if (found.PickerId != userId && !isModerator)
        {
            return QueueRemoveResult.NotYours;
        }

        song = found;
        return QueueRemoveResult.Removed;
    }

    /// <summary>Removes one waiting song, by its ID, if it's <paramref name="userId"/>'s (or they're a moderator).
    /// </summary>
    public static QueueRemoveResult Remove(MusicQueueState state, Guid songId, ulong userId, bool isModerator,
        out QueuedSong? removed)
    {
        removed = state.Songs.FirstOrDefault(s => s.Id == songId);
        if (removed is null)
        {
            return QueueRemoveResult.NotFound;
        }

        if (removed.PickerId != userId && !isModerator)
        {
            removed = null;
            return QueueRemoveResult.NotYours;
        }

        state.Songs.Remove(removed);
        Prune(state);
        return QueueRemoveResult.Removed;
    }

    /// <summary>Waiting songs whose title has <paramref name="title"/> in it, in play order: <paramref name="userId"/>'s
    /// own, or anyone's for a moderator. Compared without spaces, punctuation or case, so "Mr. Brightside" finds "Mr
    /// Brightside (Official Video)". Fewer than <see cref="ShortestTitle"/> letters find nothing: "remove a from the
    /// queue" mustn't take out whichever song has an "a" in it.</summary>
    public static List<QueuedSong> FindByTitle(MusicQueueState state, string title, ulong userId, bool isModerator)
    {
        var words = TextTools.Squash(title);
        return words.Length < ShortestTitle
            ? []
            : Order(state).Where(s => (s.PickerId == userId || isModerator) &&
                                      TextTools.Squash(s.Title).Contains(words, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The fewest letters and digits <see cref="FindByTitle"/> goes on.</summary>
    public const int ShortestTitle = 3;

    /// <summary>Removes all of a person's waiting songs; returns how many.</summary>
    public static int RemoveAllFor(MusicQueueState state, ulong userId)
    {
        var removed = state.Songs.RemoveAll(s => s.PickerId == userId);
        Prune(state);
        return removed;
    }

    public static int Clear(MusicQueueState state)
    {
        var removed = state.Songs.Count;
        state.Songs.Clear();
        Prune(state);
        return removed;
    }

    /// <summary>Where a song is in play order: 1 = next; -1 if it's not waiting.</summary>
    public static int PositionOf(MusicQueueState state, Guid songId)
        => Order(state).FindIndex(s => s.Id == songId) is var index and >= 0 ? index + 1 : -1;

    /// <summary>How long the songs ahead of this one in play order last together (not counting what's playing now),
    /// or null if it's not waiting or a live stream ahead makes that unknowable.</summary>
    public static TimeSpan? WaitBefore(MusicQueueState state, Guid songId)
    {
        var order = Order(state);
        var index = order.FindIndex(s => s.Id == songId);
        var ahead = order.Take(Math.Max(0, index)).ToList();
        return index < 0 || ahead.Any(s => s.IsLiveStream)
            ? null
            : ahead.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
    }

    /// <summary>Drops people with nothing waiting from the turns, so the list doesn't grow forever. Whoever played
    /// last stays until the turn moves on, since the next turn is worked out from where they are.</summary>
    private static void Prune(MusicQueueState state)
        => state.Participants.RemoveAll(p => p != state.LastPickerId && state.Songs.All(s => s.PickerId != p));
}
