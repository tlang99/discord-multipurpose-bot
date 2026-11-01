using Discord;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Storage;

namespace ModBot.Services.Moderation;

public class ModerationService : IModerationService
{
    private readonly IGuildSettingsStore _settingsStore;
    private readonly IWarningStore _warningStore;
    private readonly ILogger<ModerationService> _logger;

    public ModerationService(IGuildSettingsStore settingsStore, IWarningStore warningStore, ILogger<ModerationService> logger)
    {
        _settingsStore = settingsStore;
        _warningStore = warningStore;
        _logger = logger;
    }

    public async Task<ModerationResult> KickAsync(IGuildUser target, IUser moderator, string reason)
    {
        if (await GetHierarchyProblemAsync(target.Guild, target.Id, moderator, "kick") is { } problem)
        {
            return ModerationResult.Fail(problem);
        }

        try
        {
            await target.KickAsync(reason);
            await LogActionAsync(target.Guild, "Kick", Color.Orange, target, moderator, reason);
            return ModerationResult.Ok($"**{target}** has been kicked.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kick {UserId} from {GuildId}", target.Id, target.GuildId);
            return ModerationResult.Fail($"Failed to kick {target}: {ex.Message}");
        }
    }

    public async Task<ModerationResult> BanAsync(IGuild guild, IUser target, IUser moderator, string reason, int pruneDays)
    {
        if (await GetHierarchyProblemAsync(guild, target.Id, moderator, "ban") is { } problem)
        {
            return ModerationResult.Fail(problem);
        }

        try
        {
            await guild.AddBanAsync(target, pruneDays, reason);
            await LogActionAsync(guild, "Ban", Color.Red, target, moderator, reason);
            return ModerationResult.Ok($"**{target}** has been banned.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ban {UserId} from {GuildId}", target.Id, guild.Id);
            return ModerationResult.Fail($"Failed to ban {target}: {ex.Message}");
        }
    }

    public async Task<ModerationResult> UnbanAsync(IGuild guild, ulong targetUserId, IUser moderator, string reason)
    {
        try
        {
            var ban = await guild.GetBanAsync(targetUserId);
            if (ban is null)
            {
                return ModerationResult.Fail("That user is not banned.");
            }

            await guild.RemoveBanAsync(targetUserId);
            await LogActionAsync(guild, "Unban", Color.Green, ban.User, moderator, reason);
            return ModerationResult.Ok($"**{ban.User}** has been unbanned.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unban {UserId} from {GuildId}", targetUserId, guild.Id);
            return ModerationResult.Fail($"Failed to unban user: {ex.Message}");
        }
    }

    public async Task<ModerationResult> TimeoutAsync(IGuildUser target, IUser moderator, TimeSpan duration, string reason)
    {
        if (await GetHierarchyProblemAsync(target.Guild, target.Id, moderator, "time out") is { } problem)
        {
            return ModerationResult.Fail(problem);
        }

        try
        {
            await target.SetTimeOutAsync(duration, new RequestOptions { AuditLogReason = reason });
            await LogActionAsync(target.Guild, $"Timeout ({FormatDuration(duration)})", Color.Gold, target, moderator, reason);
            return ModerationResult.Ok($"**{target}** has been timed out for {FormatDuration(duration)}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to time out {UserId} in {GuildId}", target.Id, target.GuildId);
            return ModerationResult.Fail($"Failed to time out {target}: {ex.Message}");
        }
    }

    public async Task<ModerationResult> RemoveTimeoutAsync(IGuildUser target, IUser moderator, string reason)
    {
        if (await GetHierarchyProblemAsync(target.Guild, target.Id, moderator, "remove the timeout for") is { } problem)
        {
            return ModerationResult.Fail(problem);
        }

        try
        {
            await target.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = reason });
            await LogActionAsync(target.Guild, "Timeout Removed", Color.Green, target, moderator, reason);
            return ModerationResult.Ok($"Timeout removed for **{target}**.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove timeout for {UserId} in {GuildId}", target.Id, target.GuildId);
            return ModerationResult.Fail($"Failed to remove timeout for {target}: {ex.Message}");
        }
    }

    public async Task<ModerationResult> WarnAsync(IGuild guild, IUser target, IUser moderator, string reason)
    {
        try
        {
            var warning = await _warningStore.AddWarningAsync(guild.Id, target.Id, moderator.Id, reason);
            var count = (await _warningStore.GetWarningsAsync(guild.Id, target.Id)).Count;
            await LogActionAsync(guild, "Warn", Color.Gold, target, moderator, reason);
            return ModerationResult.Ok($"**{target}** has been warned. They now have **{count}** warning(s). (ID: `{warning.Id}`)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warn {UserId} in {GuildId}", target.Id, guild.Id);
            return ModerationResult.Fail($"Failed to warn {target}: {ex.Message}");
        }
    }

    public async Task LogActionAsync(IGuild guild, string action, Color color, IUser? target, IUser? moderator, string reason)
    {
        try
        {
            var settings = await _settingsStore.GetAsync(guild.Id);
            if (settings.ModLogChannelId is not ulong channelId)
            {
                return;
            }

            if (await guild.GetTextChannelAsync(channelId) is not ITextChannel channel)
            {
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle(action)
                .WithColor(color)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .AddField("User", target is null ? "N/A" : $"{target} ({target.Id})")
                .AddField("Moderator", moderator is null ? "N/A" : $"{moderator} ({moderator.Id})")
                .AddField("Reason", string.IsNullOrWhiteSpace(reason) ? "No reason provided" : reason)
                .Build();

            await channel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write mod-log entry for guild {GuildId}", guild.Id);
        }
    }

    /// <summary>
    /// Why <paramref name="moderator"/> can't act on the target, or null if they can. Discord only lets members act
    /// on people whose highest role is below their own, but the bot's role is usually near the top, so without this
    /// a moderator could use the bot to kick, ban or time out someone who outranks them (an admin, say).
    /// </summary>
    /// <param name="verb">For the message, e.g. "kick" or "time out".</param>
    private static async Task<string?> GetHierarchyProblemAsync(IGuild guild, ulong targetId, IUser moderator, string verb)
    {
        if (targetId == moderator.Id)
        {
            return $"You can't {verb} yourself.";
        }

        var bot = await guild.GetCurrentUserAsync();
        if (targetId == bot.Id)
        {
            return $"I can't {verb} myself.";
        }

        // Not in the server (e.g. banning someone who already left): there's no role to outrank.
        if (await guild.GetUserAsync(targetId) is not { } target)
        {
            return null;
        }

        // Hierarchy is the position of the member's highest role, and int.MaxValue for the server owner.
        if (moderator is not IGuildUser guildModerator || guildModerator.Hierarchy <= target.Hierarchy)
        {
            return $"You can only {verb} members whose highest role is below yours.";
        }

        if (bot.Hierarchy <= target.Hierarchy)
        {
            return $"I can't {verb} **{target}**: their highest role is at or above mine. Move my role higher in " +
                   "Server Settings → Roles.";
        }

        return null;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{duration.TotalDays:0.#} day(s)";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.#} hour(s)";
        }

        return $"{duration.TotalMinutes:0.#} minute(s)";
    }
}
