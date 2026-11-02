using System.Reflection;
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

    private static IGuildUser Member(ChannelPermissions permissions, DateTimeOffset? timedOutUntil = null)
    {
        var member = DispatchProxy.Create<IGuildUser, Fake>();
        ((Fake)(object)member).Answers = new()
        {
            ["GetPermissions"] = permissions,
            ["get_TimedOutUntil"] = timedOutUntil,
        };
        return member;
    }

    private static T Channel<T>() where T : class, IGuildChannel
    {
        var channel = DispatchProxy.Create<T, Fake>();
        ((Fake)(object)channel).Answers = new() { ["get_Name"] = "test" };
        return channel;
    }

    /// <summary>Answers the members a test sets up, by name, and refuses anything else.</summary>
    public class Fake : DispatchProxy
    {
        public Dictionary<string, object?> Answers { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod is not null && Answers.TryGetValue(targetMethod.Name, out var answer)
                ? answer
                : throw new NotSupportedException($"The fake doesn't support {targetMethod?.Name}.");
    }
}
