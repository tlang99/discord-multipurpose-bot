using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using ModBot.Services.Moderation;

namespace ModBot.Tests;

/// <summary>
/// The shared path kick, ban and the timeouts go through: the role-hierarchy check must run before anything is done,
/// so a moderator can't use the bot (whose role is usually near the top) on someone who outranks them.
/// </summary>
public sealed class ModerationServiceTests
{
    private const ulong ModeratorId = 1, TargetId = 2, BotId = 3;

    private readonly ModerationService _service =
        new(new FakeGuildSettings(), warningStore: null!, NullLogger<ModerationService>.Instance);

    [Fact]
    public async Task Moderators_cannot_act_on_themselves()
    {
        var moderator = Member(ModeratorId, hierarchy: 10);
        var target = Member(ModeratorId, hierarchy: 10, guild: Guild(bot: Member(BotId, 100), member: null));

        var result = await _service.KickAsync(target, moderator, "test");

        Assert.False(result.Success);
        Assert.Equal("You can't kick yourself.", result.Message);
        Assert.DoesNotContain("KickAsync", DiscordFake.Of(target).Calls);
    }

    [Fact]
    public async Task Moderators_cannot_act_on_someone_at_or_above_their_role()
    {
        var target = Member(TargetId, hierarchy: 10);
        target = Member(TargetId, hierarchy: 10, guild: Guild(bot: Member(BotId, 100), member: target));

        var result = await _service.KickAsync(target, Member(ModeratorId, hierarchy: 10), "test");

        Assert.False(result.Success);
        Assert.StartsWith("You can only kick members whose highest role is below yours", result.Message);
        Assert.DoesNotContain("KickAsync", DiscordFake.Of(target).Calls);
    }

    [Fact]
    public async Task The_bot_refuses_someone_at_or_above_its_own_role()
    {
        var target = Member(TargetId, hierarchy: 50);
        target = Member(TargetId, hierarchy: 50, guild: Guild(bot: Member(BotId, 50), member: target));

        var result = await _service.TimeoutAsync(target, Member(ModeratorId, hierarchy: 100), TimeSpan.FromMinutes(5),
            "test");

        Assert.False(result.Success);
        Assert.StartsWith("I can't time out", result.Message);
        Assert.DoesNotContain("SetTimeOutAsync", DiscordFake.Of(target).Calls);
    }

    [Fact]
    public async Task Acts_when_the_moderator_and_bot_both_outrank_the_target()
    {
        var target = Member(TargetId, hierarchy: 10);
        target = Member(TargetId, hierarchy: 10, guild: Guild(bot: Member(BotId, 100), member: target));

        var result = await _service.KickAsync(target, Member(ModeratorId, hierarchy: 50), "test");

        Assert.True(result.Success, result.Message);
        Assert.Contains("KickAsync", DiscordFake.Of(target).Calls);
    }

    private static IGuildUser Member(ulong id, int hierarchy, IGuild? guild = null)
        => DiscordFake.Create<IGuildUser>(new()
        {
            ["get_Id"] = id,
            ["get_Hierarchy"] = hierarchy,
            ["get_Guild"] = guild,
            ["get_GuildId"] = 99UL,
            ["KickAsync"] = Task.CompletedTask,
            ["SetTimeOutAsync"] = Task.CompletedTask,
        });

    /// <summary>A server with <paramref name="bot"/> as the bot, where looking up a member finds
    /// <paramref name="member"/> (null: they're not in the server).</summary>
    private static IGuild Guild(IGuildUser bot, IGuildUser? member)
        => DiscordFake.Create<IGuild>(new()
        {
            ["get_Id"] = 99UL,
            ["GetCurrentUserAsync"] = Task.FromResult(bot),
            ["GetUserAsync"] = Task.FromResult(member),
        });
}
