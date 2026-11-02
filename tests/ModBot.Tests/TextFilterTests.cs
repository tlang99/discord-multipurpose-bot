using Discord;
using ModBot.Models;
using ModBot.Services.Moderation;

namespace ModBot.Tests;

/// <summary>Who the word rules apply to. Auto-mod, the chatbot, reminders and song titles all use this one rule, so a
/// mistake here would open the filter everywhere at once.</summary>
public sealed class TextFilterTests
{
    private static readonly GuildSettings AutoModOn = new() { AutoModEnabled = true };
    private static readonly GuildSettings AutoModOff = new() { AutoModEnabled = false };

    [Fact]
    public void Members_with_manage_messages_are_exempt()
        => Assert.True(TextFilter.IsExempt(Member(GuildPermission.ManageMessages)));

    [Fact]
    public void Other_permissions_do_not_exempt_anyone()
        => Assert.False(TextFilter.IsExempt(Member(GuildPermission.MoveMembers, GuildPermission.KickMembers)));

    [Fact]
    public void Someone_who_is_not_a_member_is_not_exempt()
        => Assert.False(TextFilter.IsExempt(null));

    [Fact]
    public void Applies_to_ordinary_members_while_auto_mod_is_on()
        => Assert.True(TextFilter.AppliesTo(AutoModOn, Member()));

    [Fact]
    public void Never_applies_to_exempt_members()
        => Assert.False(TextFilter.AppliesTo(AutoModOn, Member(GuildPermission.ManageMessages)));

    [Fact]
    public void The_bots_own_text_is_always_checked_while_auto_mod_is_on()
        => Assert.True(TextFilter.AppliesTo(AutoModOn, author: null));

    [Fact]
    public void Nothing_is_checked_while_auto_mod_is_off()
    {
        Assert.False(TextFilter.AppliesTo(AutoModOff, Member()));
        Assert.False(TextFilter.AppliesTo(AutoModOff, author: null));
    }

    /// <summary>A member with exactly these server permissions; nothing else about them is used.</summary>
    private static IGuildUser Member(params GuildPermission[] permissions)
        => DiscordFake.Create<IGuildUser>(new()
        {
            ["get_GuildPermissions"] = new GuildPermissions(permissions.Aggregate(0UL, (all, p) => all | (ulong)p)),
        });
}
