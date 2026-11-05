namespace ModBot.Services.Voice;

/// <summary>
/// Which voice channel the bot joins by itself (voice auto-join). A bot gets one voice connection per server, so when
/// several channels have people in them it goes where the most people are, the higher one in the channel list on a
/// tie. Kept apart from Discord's types so the choice can be tested.
/// </summary>
public static class AutoJoinPicker
{
    /// <summary>A voice channel the bot could join: only ones it's allowed in and can post in the chat of.</summary>
    /// <param name="People">People in it, not counting bots.</param>
    /// <param name="Position">Its place in the channel list (lower is higher up).</param>
    public record Channel(ulong Id, int People, int Position);

    /// <param name="only">The one channel the server limited auto-join to, if it did.</param>
    /// <param name="stayOut">A channel someone told the bot to stop listening in, which it keeps out of until
    /// everyone's left it.</param>
    /// <returns>The channel to join, or null for none.</returns>
    public static ulong? Pick(IEnumerable<Channel> channels, ulong? only, ulong? stayOut)
        => channels
            .Where(c => c.People > 0 && c.Id != stayOut && (only is null || c.Id == only))
            .OrderByDescending(c => c.People)
            .ThenBy(c => c.Position)
            .Select(c => (ulong?)c.Id)
            .FirstOrDefault();
}
