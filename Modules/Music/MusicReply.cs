using Discord;

namespace ModBot.Modules.Music;

/// <summary>
/// The answer to a music request, shared by the slash commands, the music menu's buttons and the chatbot so all
/// three say the same thing. Slash commands show failures only to the caller; the chatbot replies either way.
/// </summary>
/// <param name="SuppressEmbeds">Hide link previews, for replies that link a song.</param>
/// <param name="Announce">Worth showing the whole channel when it came from a menu button. False for things the menu
/// already shows (a skip vote that didn't skip yet), which would just be noise.</param>
/// <param name="Pending">Counted, but not done yet: a skip or stop vote short of the bar. Said by voice, it's read
/// out rather than answered with the "done" chime, which would sound like the music had stopped (or skipped).</param>
public record MusicReply(bool Success, string? Text, Embed? Embed = null, bool SuppressEmbeds = false,
    bool Announce = true, bool Pending = false)
{
    public static MusicReply Fail(string text) => new(false, text);

    public static MusicReply Ok(string text, bool suppressEmbeds = true) => new(true, text, SuppressEmbeds: suppressEmbeds);

    /// <summary>A vote that was counted but hasn't reached the bar yet.</summary>
    public static MusicReply Vote(string text) => new(true, text, SuppressEmbeds: true, Announce: false, Pending: true);
}
