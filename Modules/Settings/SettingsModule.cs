using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Modules.Lfg;
using ModBot.Services.Settings;
using ModBot.Services.Storage;

namespace ModBot.Modules.Settings;

/// <summary>
/// /settings: each server's own choices for the community features: which are on, which channel music and LFG use,
/// and the LFG ping role. Everything starts on the bot's defaults (appsettings.json), so a new server works without
/// setting anything. Moderation settings stay in /automod and /modlog, and the chatbot's personality in /chat.
/// </summary>
[Group("settings", "This server's settings for the bot's features.")]
[RequireContext(ContextType.Guild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
public class SettingsModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ServerSettingsService _settings;
    private readonly ServerFeatureSwitch _switch;
    private readonly IGuildSettingsStore _moderation;
    private readonly ChatOptions _chat;
    private readonly ILogger<SettingsModule> _logger;

    public SettingsModule(ServerSettingsService settings, ServerFeatureSwitch featureSwitch,
        IGuildSettingsStore moderation, IOptions<ChatOptions> chat, ILogger<SettingsModule> logger)
    {
        _settings = settings;
        _switch = featureSwitch;
        _moderation = moderation;
        _chat = chat.Value;
        _logger = logger;
    }

    /// <summary>Slash-command names for <see cref="ServerFeature"/>, kept here so Models stays free of Discord.Net
    /// (as with the LFG choices).</summary>
    public enum FeatureChoice
    {
        [ChoiceDisplay("Chatbot (answers @mentions)")] Chat = ServerFeature.Chat,
        [ChoiceDisplay("Chatbot actions (\"play X\", \"make a poll\"...)")] ChatActions = ServerFeature.ChatActions,
        [ChoiceDisplay("Music")] Music = ServerFeature.Music,
        [ChoiceDisplay("LFG")] Lfg = ServerFeature.Lfg,
        [ChoiceDisplay("Polls")] Polls = ServerFeature.Polls,
        [ChoiceDisplay("Reminders")] Reminders = ServerFeature.Reminders,
    }

    /// <summary>The features with their own channel.</summary>
    public enum ChannelFeature
    {
        [ChoiceDisplay("Music")] Music,
        [ChoiceDisplay("LFG")] Lfg,
    }

    [SlashCommand("show", "Shows this server's settings for every feature.")]
    public async Task ShowAsync()
    {
        var server = await _settings.GetAsync(Context.Guild.Id);
        var moderation = await _moderation.GetAsync(Context.Guild.Id);

        var features = new StringBuilder();
        foreach (var feature in Enum.GetValues<ServerFeature>())
        {
            var on = server.IsEnabled(feature);
            var note = feature is ServerFeature.Chat or ServerFeature.ChatActions && !_chat.IsEnabled
                ? " (but the chatbot is off for every server in the bot's own settings)"
                : "";
            features.AppendLine($"{(on ? "✅" : "🚫")} {ServerSettingsService.NameOf(feature)}{note}");
        }

        var ping = await _settings.LfgPingRoleAsync(Context.Guild);
        var pingText = ping.Off ? "off"
            : ping.Role is { } role ? role.Mention
            : ping.Name.Length > 0 ? $"@{ping.Name} (not created yet: run `/lfg-setup`)"
            : "(the chosen role was deleted)";

        var embed = new EmbedBuilder()
            .WithColor(Colors.Bot)
            .WithTitle($"Settings for {Context.Guild.Name}")
            .AddField("Features", features.ToString())
            .AddField("Channels",
                $"Music: {await DescribeAsync(ChannelFeature.Music)}\n" +
                $"LFG: {await DescribeAsync(ChannelFeature.Lfg)}")
            .AddField("LFG pings", $"{pingText}{(server.LfgPingRoleId is null && !server.LfgPingsOff ? " (default)" : "")}")
            .AddField("Auto-mod",
                $"{(moderation.AutoModEnabled ? "✅ On" : "🚫 Off")} - change it and the filters with `/automod`, and " +
                "the log channel with `/modlog`.")
            .WithFooter("Change these with /settings feature, /settings channel and /settings lfg-pings. " +
                        "Admins can also ask me, e.g. \"@bot disable polls\".")
            .Build();
        await RespondAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("feature", "Turns one of the bot's features on or off for this server.")]
    public async Task FeatureAsync(
        [Summary("feature", "Which feature")] FeatureChoice feature,
        [Summary("enabled", "On or off")] bool enabled)
        => await RespondAsync(await _switch.SetAsync(Context.Guild, (ServerFeature)feature, enabled, Context.User),
            ephemeral: true);

    [SlashCommand("channel", "Limits music or LFG to one channel. Leave the channel out to allow any channel.")]
    public async Task ChannelAsync(
        [Summary("feature", "Music or LFG")] ChannelFeature feature,
        [Summary("channel", "Only this channel (leave out: any channel)")] [ChannelTypes(ChannelType.Text)]
        ITextChannel? channel = null)
    {
        // Music announcements and LFG cards are posted there, so the bot has to be able to post there.
        if (channel is not null &&
            ChannelAccess.WhyBotCantPostEmbeds(Context.Guild.CurrentUser, channel, NameOf(feature)) is { } problem)
        {
            await RespondAsync(problem, ephemeral: true);
            return;
        }

        await _settings.UpdateAsync(Context.Guild.Id, s =>
        {
            (feature == ChannelFeature.Music ? s.Music : s.Lfg).ChannelId = channel?.Id;
        });

        var now = await DescribeAsync(feature);
        _logger.LogInformation("{Feature} channel set to {Channel} by {User} ({UserId}).", NameOf(feature),
            channel is not null ? $"#{channel.Name} ({channel.Id})" : "any channel", Context.User.Username,
            Context.User.Id);
        await RespondAsync($"{NameOf(feature)}: {now}.", ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("lfg-pings", "Sets the role LFG posts ping. Leave both options out to go back to the default.")]
    public async Task LfgPingsAsync(
        [Summary("role", "Ping this role")] IRole? role = null,
        [Summary("off", "True: no pings")] bool? off = null)
    {
        if (role is not null && off == true)
        {
            await RespondAsync("Pick a role *or* off, not both.", ephemeral: true);
            return;
        }

        // Members give themselves this role from the /lfg-setup panel, so it mustn't be one that carries power.
        if (role is not null && LfgPoster.WhyUnsafeToSelfAssign(Context.Guild, role) is { } reason)
        {
            await RespondAsync($"{role.Mention} can't be the ping role: it {reason}. Members give the ping role to " +
                               "themselves, so it has to be a plain role. Leave the role out to use the default, " +
                               "which `/lfg-setup` creates.", ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        await _settings.UpdateAsync(Context.Guild.Id, s =>
        {
            s.LfgPingRoleId = role?.Id;
            s.LfgPingsOff = off == true;
        });
        _logger.LogInformation("LFG ping role set to {Role} by {User} ({UserId}).",
            role is not null ? $"@{role.Name} ({role.Id})" : off == true ? "off" : "the default",
            Context.User.Username, Context.User.Id);

        var now = off == true ? "off"
            : role is not null ? $"{role.Mention}. Run `/lfg-setup` to post the panel members use to get it"
            : $"back to the default (@{_settings.DefaultPingRoleName})";
        await RespondAsync($"LFG pings: {now}.", ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private static string NameOf(ChannelFeature feature) => feature == ChannelFeature.Music ? "Music" : "LFG";

    private async Task<string> DescribeAsync(ChannelFeature feature)
    {
        var where = feature == ChannelFeature.Music
            ? await _settings.MusicChannelAsync(Context.Guild)
            : await _settings.LfgChannelAsync(Context.Guild);
        return where.Anywhere ? "any channel (pick one with `/settings channel`)"
            : where.Channel?.Mention ?? "(the chosen channel was deleted - pick another with `/settings channel`)";
    }
}
