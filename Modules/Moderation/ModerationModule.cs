using Discord;
using Discord.Interactions;
using ModBot.Services.Moderation;
using ModBot.Services.Storage;

namespace ModBot.Modules.Moderation;

[RequireContext(ContextType.Guild)]
public class ModerationModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IModerationService _moderationService;
    private readonly IWarningStore _warningStore;

    public ModerationModule(IModerationService moderationService, IWarningStore warningStore)
    {
        _moderationService = moderationService;
        _warningStore = warningStore;
    }

    [SlashCommand("kick", "Kicks a member from the server.")]
    [RequireUserPermission(GuildPermission.KickMembers)]
    [RequireBotPermission(GuildPermission.KickMembers)]
    public async Task KickAsync(
        IGuildUser user,
        [Summary(description: "Why this member is being kicked.")] string reason = "No reason provided")
    {
        await DeferAsync();
        var result = await _moderationService.KickAsync(user, Context.User, reason);
        await FollowupAsync(result.Message);
    }

    [SlashCommand("ban", "Bans a user from the server.")]
    [RequireUserPermission(GuildPermission.BanMembers)]
    [RequireBotPermission(GuildPermission.BanMembers)]
    public async Task BanAsync(
        IUser user,
        [Summary(description: "Why this user is being banned.")] string reason = "No reason provided",
        [Summary("delete-message-days", "Days of message history to delete (0-7).")]
        [MinValue(0)][MaxValue(7)] int deleteMessageDays = 0)
    {
        await DeferAsync();
        var result = await _moderationService.BanAsync(Context.Guild, user, Context.User, reason, deleteMessageDays);
        await FollowupAsync(result.Message);
    }

    [SlashCommand("unban", "Unbans a user by their ID.")]
    [RequireUserPermission(GuildPermission.BanMembers)]
    [RequireBotPermission(GuildPermission.BanMembers)]
    public async Task UnbanAsync(
        [Summary("user-id", "The Discord ID of the user to unban.")] string userId,
        [Summary(description: "Why this user is being unbanned.")] string reason = "No reason provided")
    {
        await DeferAsync();

        if (!ulong.TryParse(userId, out var parsedId))
        {
            await FollowupAsync("That doesn't look like a valid user ID.");
            return;
        }

        var result = await _moderationService.UnbanAsync(Context.Guild, parsedId, Context.User, reason);
        await FollowupAsync(result.Message);
    }

    [SlashCommand("timeout", "Temporarily restricts a member from interacting in the server.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task TimeoutAsync(
        IGuildUser user,
        [Summary(description: "Duration of the timeout, in minutes (max 40320 = 28 days).")]
        [MinValue(1)][MaxValue(40320)] int minutes,
        [Summary(description: "Why this member is being timed out.")] string reason = "No reason provided")
    {
        await DeferAsync();
        var duration = TimeSpan.FromMinutes(minutes);
        var result = await _moderationService.TimeoutAsync(user, Context.User, duration, reason);
        await FollowupAsync(result.Message);
    }

    [SlashCommand("untimeout", "Removes an active timeout from a member.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [RequireBotPermission(GuildPermission.ModerateMembers)]
    public async Task RemoveTimeoutAsync(
        IGuildUser user,
        [Summary(description: "Why this timeout is being removed.")] string reason = "No reason provided")
    {
        await DeferAsync();
        var result = await _moderationService.RemoveTimeoutAsync(user, Context.User, reason);
        await FollowupAsync(result.Message);
    }

    [SlashCommand("warn", "Issues a warning to a member.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task WarnAsync(
        IUser user,
        [Summary(description: "Why this member is being warned.")] string reason = "No reason provided")
    {
        await DeferAsync();
        var result = await _moderationService.WarnAsync(Context.Guild, user, Context.User, reason);
        await FollowupAsync(result.Message);
    }

    [SlashCommand("warnings", "Lists the warnings a member has received.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task WarningsAsync(IUser user)
    {
        await DeferAsync();
        var warnings = await _warningStore.GetWarningsAsync(Context.Guild.Id, user.Id);

        if (warnings.Count == 0)
        {
            await FollowupAsync($"**{user}** has no warnings.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Warnings for {user}")
            .WithColor(Color.Gold);

        foreach (var warning in warnings.OrderByDescending(w => w.TimestampUtc).Take(25))
        {
            embed.AddField(
                $"{warning.TimestampUtc:yyyy-MM-dd HH:mm} UTC — ID {warning.Id.ToString()[..8]}",
                $"{warning.Reason}\n— by <@{warning.ModeratorId}>");
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("clearwarnings", "Clears all warnings for a member.")]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    public async Task ClearWarningsAsync(IUser user)
    {
        await DeferAsync();
        var removed = await _warningStore.ClearWarningsAsync(Context.Guild.Id, user.Id);
        await FollowupAsync(removed == 0
            ? $"**{user}** had no warnings to clear."
            : $"Cleared **{removed}** warning(s) for **{user}**.");
    }

    [SlashCommand("purge", "Bulk-deletes recent messages in this channel.")]
    [RequireUserPermission(GuildPermission.ManageMessages)]
    [RequireBotPermission(GuildPermission.ManageMessages)]
    public async Task PurgeAsync(
        [Summary(description: "Number of messages to delete (1-100).")]
        [MinValue(1)][MaxValue(100)] int amount)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Channel is not ITextChannel channel)
        {
            await FollowupAsync("This command can only be used in a text channel.", ephemeral: true);
            return;
        }

        var messages = await channel.GetMessagesAsync(amount).FlattenAsync();
        var deletable = messages.Where(m => DateTimeOffset.UtcNow - m.Timestamp < TimeSpan.FromDays(14)).ToList();

        if (deletable.Count == 0)
        {
            await FollowupAsync("No messages young enough to bulk-delete were found.", ephemeral: true);
            return;
        }

        await channel.DeleteMessagesAsync(deletable);
        await _moderationService.LogActionAsync(
            Context.Guild,
            "Purge",
            Color.Orange,
            target: null,
            moderator: Context.User,
            reason: $"Deleted {deletable.Count} message(s) in #{channel.Name}");

        await FollowupAsync($"Deleted **{deletable.Count}** message(s).", ephemeral: true);
    }
}
