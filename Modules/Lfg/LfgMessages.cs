using Discord;
using ModBot.Models;

namespace ModBot.Modules.Lfg;

/// <summary>Builds the LFG post's embed and buttons for each state it can be in.</summary>
public static class LfgMessages
{
    // Button IDs carry the post's ID, so clicks go straight to the right post: "lfg-join:<guid>".
    public const string JoinPrefix = "lfg-join";
    public const string LeavePrefix = "lfg-leave";
    public const string StartPrefix = "lfg-start";
    public const string CancelPrefix = "lfg-cancel";

    private static readonly Color OpenColor = new(0x3BA55D);
    private static readonly Color ClosedColor = new(0x747F8D);

    public static string ModeLabel(LfgMode mode) => mode switch
    {
        LfgMode.Multiplayer => "Multiplayer",
        _ => mode.ToString(),
    };

    public static Embed OpenEmbed(LfgPost post)
    {
        var spots = post.OpenSpots == 1 ? "1 more player" : $"{post.OpenSpots} more players";
        return Base(post, OpenColor, $"🎮 {ModeLabel(post.Mode)} — looking for {spots}")
            .AddField("Expires", TimestampTag.FormatFromDateTimeOffset(post.ExpiresAt, TimestampTagStyles.Relative),
                inline: true)
            .Build();
    }

    public static MessageComponent OpenButtons(LfgPost post) => new ComponentBuilder()
        .WithButton("Join", $"{JoinPrefix}:{post.Id:N}", ButtonStyle.Success, new Emoji("➕"))
        .WithButton("Leave", $"{LeavePrefix}:{post.Id:N}", ButtonStyle.Secondary)
        .WithButton("Start now", $"{StartPrefix}:{post.Id:N}", ButtonStyle.Primary)
        .WithButton("Cancel", $"{CancelPrefix}:{post.Id:N}", ButtonStyle.Danger)
        .Build();

    public static Embed ReadyEmbed(LfgPost post, bool full)
        => Base(post, ClosedColor, $"✅ {ModeLabel(post.Mode)} — squad {(full ? "full" : "started")}").Build();

    public static Embed CancelledEmbed(LfgPost post, bool byModerator)
        => Base(post, ClosedColor, $"❌ {ModeLabel(post.Mode)} — {(byModerator ? "removed by a moderator" : "cancelled")}")
            .Build();

    public static Embed ExpiredEmbed(LfgPost post)
        => Base(post, ClosedColor, $"⌛ {ModeLabel(post.Mode)} — expired").Build();

    /// <summary>For buttons on a post the bot no longer knows about (closed already, or from before a restart).</summary>
    public static Embed InactiveEmbed(Embed? original)
    {
        var builder = original?.ToEmbedBuilder() ?? new EmbedBuilder();
        return builder.WithColor(ClosedColor).WithFooter("This LFG post is no longer active.").Build();
    }

    /// <summary>The message that pings everyone in a ready squad. Only those users can be mentioned.</summary>
    public static (string Text, AllowedMentions Mentions) ReadyPing(LfgPost post, bool full)
    {
        var mentions = string.Join(" ", post.Members.Select(MentionUtils.MentionUser));
        var text = $"{mentions}\n🎮 Your {ModeLabel(post.Mode)} squad is {(full ? "full" : "ready")} " +
                   $"({post.Members.Count}/{post.Size}) — hop in voice and get on!";
        return (text, new AllowedMentions(AllowedMentionTypes.None) { UserIds = [.. post.Members] });
    }

    private static EmbedBuilder Base(LfgPost post, Color color, string title)
    {
        var members = string.Join("\n", post.Members.Select((id, i) =>
            $"{i + 1}. {MentionUtils.MentionUser(id)}{(id == post.HostId ? " (host)" : "")}"));

        var builder = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .AddField($"Squad ({post.Members.Count}/{post.Size})", members, inline: true);

        if (!string.IsNullOrWhiteSpace(post.Note))
        {
            builder.WithDescription(Format.Sanitize(post.Note));
        }

        return builder;
    }
}
