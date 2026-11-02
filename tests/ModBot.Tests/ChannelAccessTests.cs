using Discord;
using ModBot.Common;

namespace ModBot.Tests;

/// <summary><see cref="ChannelAccess.CanPost"/>: whether someone may post in a channel. Reminders (set and
/// delivered), the chatbot and LFG-by-chat all rely on it, so the bot never posts where the person couldn't.</summary>
public sealed class ChannelAccessTests
{
    private static readonly ChannelPermissions SeeAndSend =
        new(viewChannel: true, sendMessages: true, sendMessagesInThreads: false);

    private static readonly ChannelPermissions SeeAndSendInThreads =
        new(viewChannel: true, sendMessages: false, sendMessagesInThreads: true);

    [Fact]
    public void Can_post_with_view_and_send()
        => Assert.True(ChannelAccess.CanPost(Member(SeeAndSend), Channel<ITextChannel>()));

    [Fact]
    public void Cannot_post_without_view_channel()
        => Assert.False(ChannelAccess.CanPost(Member(new ChannelPermissions(sendMessages: true)), Channel<ITextChannel>()));

    [Fact]
    public void Cannot_post_without_send_messages()
        => Assert.False(ChannelAccess.CanPost(Member(new ChannelPermissions(viewChannel: true)), Channel<ITextChannel>()));

    [Fact]
    public void Threads_need_send_in_threads_rather_than_send()
    {
        Assert.True(ChannelAccess.CanPost(Member(SeeAndSendInThreads), Channel<IThreadChannel>()));
        Assert.False(ChannelAccess.CanPost(Member(SeeAndSend), Channel<IThreadChannel>()));
        Assert.False(ChannelAccess.CanPost(Member(SeeAndSendInThreads), Channel<ITextChannel>()));
    }

    [Fact]
    public void A_timed_out_member_cannot_post_anywhere()
        => Assert.False(ChannelAccess.CanPost(Member(SeeAndSend, DateTimeOffset.UtcNow.AddMinutes(10)),
            Channel<ITextChannel>()));

    [Fact]
    public void An_expired_timeout_does_not_count()
        => Assert.True(ChannelAccess.CanPost(Member(SeeAndSend, DateTimeOffset.UtcNow.AddMinutes(-10)),
            Channel<ITextChannel>()));

    // WhyCantPostPanel: /help-post, /music-menu and /lfg-setup. Manage Server alone mustn't let someone have the bot
    // post and pin where they couldn't themselves.

    private static readonly ChannelPermissions BotCanPostEmbeds =
        new(viewChannel: true, sendMessages: true, embedLinks: true);

    private static readonly ChannelPermissions UserCanPostAndPin =
        new(viewChannel: true, sendMessages: true, pinMessages: true);

    [Fact]
    public void Panel_can_be_posted_when_both_have_the_permissions()
        => Assert.Null(ChannelAccess.WhyCantPostPanel(Member(BotCanPostEmbeds), Member(UserCanPostAndPin),
            Channel<ITextChannel>(), "the panel"));

    [Fact]
    public void Panel_needs_the_bot_to_send_embeds()
        => Assert.StartsWith("I need **Send Messages** and **Embed Links**", ChannelAccess.WhyCantPostPanel(
            Member(SeeAndSend), Member(UserCanPostAndPin), Channel<ITextChannel>(), "the panel"));

    [Fact]
    public void Panel_needs_the_person_to_be_able_to_pin_there()
        => Assert.StartsWith("You need **Send Messages** and **Pin Messages**", ChannelAccess.WhyCantPostPanel(
            Member(BotCanPostEmbeds), Member(SeeAndSend), Channel<ITextChannel>(), "the panel"));

    [Fact]
    public void Panel_is_refused_for_someone_who_is_not_a_member()
        => Assert.StartsWith("You need", ChannelAccess.WhyCantPostPanel(
            Member(BotCanPostEmbeds), user: null, Channel<ITextChannel>(), "the panel"));

    [Fact]
    public void Panel_only_goes_in_text_channels()
        => Assert.NotNull(ChannelAccess.WhyCantPostPanel(Member(BotCanPostEmbeds), Member(UserCanPostAndPin),
            DiscordFake.Create<ICategoryChannel>(new()), "the panel"));

    private static IGuildUser Member(ChannelPermissions permissions, DateTimeOffset? timedOutUntil = null)
        => DiscordFake.Create<IGuildUser>(new()
        {
            ["GetPermissions"] = permissions,
            ["get_TimedOutUntil"] = timedOutUntil,
        });

    private static T Channel<T>() where T : class, IGuildChannel
        => DiscordFake.Create<T>(new() { ["get_Name"] = "test" });
}
