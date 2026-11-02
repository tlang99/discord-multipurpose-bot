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

    public Task<ModerationResult> KickAsync(IGuildUser target, IUser moderator, string reason)
        => ActAsync(target.Guild, target, moderator, reason, "kick", () => target.KickAsync(reason),
            "Kick", Color.Orange, $"**{target}** has been kicked.");

    public Task<ModerationResult> BanAsync(IGuild guild, IUser target, IUser moderator, string reason, int pruneDays)
        => ActAsync(guild, target, moderator, reason, "ban", () => guild.AddBanAsync(target, pruneDays, reason),
            "Ban", Color.Red, $"**{target}** has been banned.");

    public Task<ModerationResult> TimeoutAsync(IGuildUser target, IUser moderator, TimeSpan duration, string reason)
        => ActAsync(target.Guild, target, moderator, reason, "time out",
            () => target.SetTimeOutAsync(duration, new RequestOptions { AuditLogReason = reason }),
            $"Timeout ({FormatDuration(duration)})", Color.Gold,
            $"**{target}** has been timed out for {FormatDuration(duration)}.");

    public Task<ModerationResult> RemoveTimeoutAsync(IGuildUser target, IUser moderator, string reason)
        => ActAsync(target.Guild, target, moderator, reason, "remove the timeout for",
            () => target.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = reason }),
            "Timeout Removed", Color.Green, $"Timeout removed for **{target}**.");

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
    /// What kick, ban and the timeouts have in common: check the moderator may act on the target, do it, write the
    /// mod-log entry, and turn a Discord error into a message for the moderator.
    /// </summary>
    /// <param name="verb">For the messages, e.g. "kick" or "time out".</param>
    /// <param name="logTitle">The mod-log entry's title, e.g. "Kick".</param>
    private async Task<ModerationResult> ActAsync(IGuild guild, IUser target, IUser moderator, string reason,
        string verb, Func<Task> action, string logTitle, Color logColor, string success)
    {
        if (await GetHierarchyProblemAsync(guild, target.Id, moderator, verb) is { } problem)
        {
            return ModerationResult.Fail(problem);
        }

        try
        {
            await action();
            await LogActionAsync(guild, logTitle, logColor, target, moderator, reason);
            return ModerationResult.Ok(success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Verb} {UserId} in {GuildId}", verb, target.Id, guild.Id);
            return ModerationResult.Fail($"Failed to {verb} {target}: {ex.Message}");
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

        // Not in the server (e.g. banning someone who already left): there's no role to outrank. AllowDownload asks
        // Discord when the member isn't cached, so a cache miss can't skip the check.
        if (await guild.GetUserAsync(targetId, CacheMode.AllowDownload) is not { } target)
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
