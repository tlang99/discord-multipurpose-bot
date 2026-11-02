using Discord;

namespace ModBot.Modules.Music;

/// <summary>
/// The answer to a music request, shared by the slash commands, the session menu's buttons and the chatbot so all
/// three say the same thing. Slash commands show failures only to the caller; the chatbot replies either way.
/// </summary>
/// <param name="SuppressEmbeds">Hide link previews, for replies that link a song.</param>
/// <param name="Announce">Worth showing the whole channel when it came from a menu button. False for things the menu
/// already shows (a skip vote that didn't skip yet), which would just be noise.</param>
public record MusicReply(bool Success, string? Text, Embed? Embed = null, bool SuppressEmbeds = false,
    bool Announce = true)
{
    public static MusicReply Fail(string text) => new(false, text);

    public static MusicReply Ok(string text, bool suppressEmbeds = true) => new(true, text, SuppressEmbeds: suppressEmbeds);
}

public static class MusicPermissions
{
    /// <summary>Moderators can control the player from anywhere, e.g. to stop an offensive song without joining the
    /// voice channel. Move Members/Manage Messages match the permissions they already use for voice/text moderation.</summary>
    public static bool IsModerator(IGuildUser? user)
        => user is not null && (user.GuildPermissions.MoveMembers || user.GuildPermissions.ManageMessages);
}
