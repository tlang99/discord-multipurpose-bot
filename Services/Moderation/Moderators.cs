using Discord;

namespace ModBot.Services.Moderation;

/// <summary>
/// Who counts as a moderator for the community features (music, LFG): members with Move Members or Manage Messages,
/// the permissions they already use for voice and text moderation. They can control the music from anywhere (e.g. to
/// stop an offensive song without joining voice), skip any song outright, remove anyone's songs or clear the queue,
/// and cancel anyone's LFG post. Server-level permissions, so channel overrides don't change it, and administrators
/// always count.
/// </summary>
public static class Moderators
{
    public static bool IsModerator(IGuildUser? user)
        => user is not null && (user.GuildPermissions.MoveMembers || user.GuildPermissions.ManageMessages);
}
