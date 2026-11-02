namespace ModBot.Modules.Music;

/// <summary>
/// The turn-taking order for a music session: one song from each person in the order they joined, then round again,
/// so someone who queues ten songs doesn't hold everyone else up. Each person's own songs keep the order they added
/// them in.
/// </summary>
public static class FairOrder
{
    /// <param name="items">The songs waiting to play, in any order (each person's in the order they added them).</param>
    /// <param name="pickerOf">Who picked a song.</param>
    /// <param name="participants">Everyone in the session, in the order they joined.</param>
    /// <param name="lastPicker">Whose song is playing (or played last): the next turn goes to the person after
    /// them. Null starts from the first person.</param>
    public static List<T> Arrange<T>(IEnumerable<T> items, Func<T, ulong> pickerOf, IReadOnlyList<ulong> participants,
        ulong? lastPicker)
    {
        var byPicker = new Dictionary<ulong, Queue<T>>();
        var pickers = new List<ulong>();
        foreach (var item in items)
        {
            var picker = pickerOf(item);
            if (!byPicker.TryGetValue(picker, out var own))
            {
                byPicker[picker] = own = new Queue<T>();
                pickers.Add(picker);
            }

            own.Enqueue(item);
        }

        // Turn order: participants from the one after the last picker, wrapping round; then anyone with songs who
        // isn't a participant (shouldn't happen, but their songs mustn't vanish).
        var start = lastPicker is { } last && IndexOf(participants, last) is var index and >= 0 ? index + 1 : 0;
        var turns = Enumerable.Range(0, participants.Count)
            .Select(i => participants[(start + i) % participants.Count])
            .Concat(pickers.Where(p => !participants.Contains(p)))
            .Where(byPicker.ContainsKey)
            .ToList();

        // Round after round, each person in turn gives their next song, until everyone's run out.
        var result = new List<T>();
        bool progressed;
        do
        {
            progressed = false;
            foreach (var picker in turns)
            {
                if (byPicker[picker].TryDequeue(out var next))
                {
                    result.Add(next);
                    progressed = true;
                }
            }
        }
        while (progressed);

        return result;
    }

    private static int IndexOf(IReadOnlyList<ulong> list, ulong value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
