using Discord;
using ModBot.Models;

namespace ModBot.Services.Moderation;

/// <summary>
/// Encapsulates moderation actions and their mod-log side effects, independent
/// of the slash-command module that invokes them. Keeping this logic out of the
/// Discord.Net interaction module means it can be reused by other entry points
/// later (automod, a web dashboard, etc.) without duplicating it.
/// </summary>
public interface IModerationService
{
    Task<ModerationResult> KickAsync(IGuildUser target, IUser moderator, string reason);

    Task<ModerationResult> BanAsync(IGuild guild, IUser target, IUser moderator, string reason, int pruneDays);

    Task<ModerationResult> UnbanAsync(IGuild guild, ulong targetUserId, IUser moderator, string reason);

    Task<ModerationResult> TimeoutAsync(IGuildUser target, IUser moderator, TimeSpan duration, string reason);

    Task<ModerationResult> RemoveTimeoutAsync(IGuildUser target, IUser moderator, string reason);

    Task<ModerationResult> WarnAsync(IGuild guild, IUser target, IUser moderator, string reason);

    Task LogActionAsync(IGuild guild, string action, Color color, IUser? target, IUser? moderator, string reason);
}
