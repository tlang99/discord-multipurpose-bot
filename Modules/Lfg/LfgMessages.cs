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

    // The opt-in panel's buttons. Not per post, so no ID suffix.
    public const string RoleOnId = "lfg-role-on";
    public const string RoleOffId = "lfg-role-off";

    private static readonly Color OpenColor = new(0x3BA55D);
    private static readonly Color ClosedColor = new(0x747F8D);

    public static string PlatformLabel(LfgPlatform platform) => platform switch
    {
        LfgPlatform.PcOnly => "PC only",
        LfgPlatform.ConsoleOnly => "Console only",
        _ => "Crossplay",
    };

    /// <summary>The playlist the post is for ("Ranked Play"), or "LFG" when any goes. Playlist names are the server's
    /// own (/settings lfg-game), so they're escaped for Discord formatting.</summary>
    private static string Heading(LfgPost post) => post.Details.Playlist is { } playlist ? Format.Sanitize(playlist) : "LFG";

    /// <summary>"Ranked Play squad", or just "squad".</summary>
    private static string Squad(LfgPost post)
        => post.Details.Playlist is { } playlist ? $"{Format.Sanitize(playlist)} squad" : "squad";

    /// <summary>"🏆 Gold+ · 🎧 Mic required · 🖥️ PC only" - only the details the host actually set. The game's top
    /// rank has no "+" ("🏆 Top 250"): there's nothing above it.</summary>
    public static string? Tags(LfgDetails details)
    {
        var tags = new List<string>();
        if (details.MinRank is { } rank)
        {
            tags.Add(details.MinRankIsTop ? $"🏆 {Format.Sanitize(rank)}" : $"🏆 {Format.Sanitize(rank)}+");
        }

        if (details.MicRequired is { } mic)
        {
            tags.Add(mic ? "🎧 Mic required" : "🔇 Mic optional");
        }

        if (details.Platform != LfgPlatform.Crossplay)
        {
            tags.Add($"🖥️ {PlatformLabel(details.Platform)}");
        }

        return tags.Count == 0 ? null : string.Join(" · ", tags);
    }

    public static Embed OpenEmbed(LfgPost post)
    {
        var spots = post.OpenSpots == 1 ? "1 more player" : $"{post.OpenSpots} more players";
        return Base(post, OpenColor, $"🎮 {Heading(post)} — looking for {spots}")
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
        => Base(post, ClosedColor, $"✅ {Heading(post)} — squad {(full ? "full" : "started")}").Build();

    public static Embed CancelledEmbed(LfgPost post, bool byModerator)
        => Base(post, ClosedColor, $"❌ {Heading(post)} — {(byModerator ? "removed by a moderator" : "cancelled")}")
            .Build();

    public static Embed ExpiredEmbed(LfgPost post)
        => Base(post, ClosedColor, $"⌛ {Heading(post)} — expired").Build();

    /// <summary>The text above a new post that pings the LFG role. Only that role can be mentioned.</summary>
    public static (string Text, AllowedMentions Mentions) RolePing(LfgPost post, ulong roleId)
    {
        var spots = post.OpenSpots == 1 ? "1 spot" : $"{post.OpenSpots} spots";
        return ($"{MentionUtils.MentionRole(roleId)} {MentionUtils.MentionUser(post.HostId)} is looking for a " +
                $"{Squad(post)} ({spots} open).",
            new AllowedMentions(AllowedMentionTypes.None) { RoleIds = [roleId] });
    }

    public static Embed RolePanelEmbed(string roleName) => new EmbedBuilder()
        .WithColor(OpenColor)
        .WithTitle("🔔 LFG pings")
        .WithDescription($"Want a ping whenever someone posts an LFG here? Grab the **@{Format.Sanitize(roleName)}** " +
                         "role below. You can turn it off again any time.")
        .Build();

    public static MessageComponent RolePanelButtons() => new ComponentBuilder()
        .WithButton("Get LFG pings", RoleOnId, ButtonStyle.Success, new Emoji("🔔"))
        .WithButton("Stop LFG pings", RoleOffId, ButtonStyle.Secondary, new Emoji("🔕"))
        .Build();

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
        var text = $"{mentions}\n🎮 Your {Squad(post)} is {(full ? "full" : "ready")} " +
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

        // Tags on the first line, the host's note under them.
        var description = string.Join("\n", new[]
        {
            Tags(post.Details),
            string.IsNullOrWhiteSpace(post.Note) ? null : Format.Sanitize(post.Note),
        }.Where(line => line is not null));

        if (description.Length > 0)
        {
            builder.WithDescription(description);
        }

        return builder;
    }
}
