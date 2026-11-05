using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Modules.Lfg;
using ModBot.Services.Chat;
using ModBot.Services.Lfg;
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
    private readonly ChatPersonality _personality;
    private readonly ILogger<SettingsModule> _logger;

    public SettingsModule(ServerSettingsService settings, ServerFeatureSwitch featureSwitch,
        IGuildSettingsStore moderation, IOptions<ChatOptions> chat, ChatPersonality personality,
        ILogger<SettingsModule> logger)
    {
        _settings = settings;
        _switch = featureSwitch;
        _moderation = moderation;
        _chat = chat.Value;
        _personality = personality;
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
        var personality = await _personality.GetWithSourceAsync(Context.Guild.Id);

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
            .AddField("LFG game", server.LfgGame is { IsEmpty: false } game
                ? $"{TextTools.Count(game.Playlists.Count, "playlist")}, {TextTools.Count(game.Ranks.Count, "rank")} " +
                  "(change with `/settings lfg-game`)"
                : "not set up: `/lfg` has no playlist or rank options yet (`/settings lfg-game`)")
            // Embed fields hold 1024 characters; a personality can be up to 1000, plus the note under it.
            .AddField("Chatbot personality",
                $"{TextTools.Truncate(personality.Text, 900)}\n-# " +
                (personality.IsCustom
                    ? "This server's own - change it with `/chat personality set`, or `reset` for the default."
                    : "The default - give this server its own with `/chat personality set`."))
            .AddField("Auto-mod",
                $"{(moderation.AutoModEnabled ? "✅ On" : "🚫 Off")} - change it and the filters with `/automod`, and " +
                "the log channel with `/modlog`.")
            .WithFooter("Change these with /settings feature, /settings channel and /settings lfg-pings. " +
                        "Admins can also ask me, e.g. \"@bot disable polls\".")
            .Build();
        await RespondAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private const string LfgGameModalId = "settings-lfg-game";

    [SlashCommand("lfg-game", "Sets the playlists/modes and ranks /lfg offers here, in a pop-up. Blank until set.")]
    public async Task LfgGameAsync()
    {
        var (playlists, ranks) = LfgGameProfileText.Format(await _settings.LfgGameAsync(Context.Guild.Id));
        var modal = new ModalBuilder()
            .WithTitle(LfgGameModal.ModalTitle)
            .WithCustomId(LfgGameModalId)
            .AddTextInput(LfgGameModal.PlaylistsLabel, LfgGameModal.PlaylistsId, TextInputStyle.Paragraph,
                placeholder: LfgGameModal.PlaylistsPlaceholder, maxLength: LfgGameProfileText.MaxBoxLength,
                required: false, value: playlists)
            .AddTextInput(LfgGameModal.RanksLabel, LfgGameModal.RanksId, TextInputStyle.Paragraph,
                placeholder: LfgGameModal.RanksPlaceholder, maxLength: LfgGameProfileText.MaxBoxLength,
                required: false, value: ranks)
            .Build();
        await RespondWithModalAsync(modal);
    }

    // ignoreGroupNames: the modal's ID is a plain string, not prefixed with "settings" (see ChatModule's pop-up).
    [ModalInteraction(LfgGameModalId, ignoreGroupNames: true)]
    public async Task OnLfgGameSubmittedAsync(LfgGameModal modal)
    {
        var (game, problem) = LfgGameProfileText.Parse(modal.Playlists, modal.Ranks);
        if (game is null)
        {
            await RespondAsync($"{problem}\nNothing was changed - run `/settings lfg-game` again to fix it.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        await _settings.UpdateAsync(Context.Guild.Id, s => s.LfgGame = game.IsEmpty ? null : game);
        _logger.LogInformation("LFG game set to {Playlists} playlist(s), {Ranks} rank(s) by {User} ({UserId}).",
            game.Playlists.Count, game.Ranks.Count, Context.User.Username, Context.User.Id);

        await RespondAsync(game.IsEmpty
            ? "Cleared: `/lfg` here has no playlist or rank options now (squad size, mic, platform and notes still work)."
            : $"Saved: {TextTools.Count(game.Playlists.Count, "playlist")} and {TextTools.Count(game.Ranks.Count, "rank")}. " +
              "`/lfg` suggests them as you type, and chat understands them (\"make an lfg for …\").",
            ephemeral: true, allowedMentions: AllowedMentions.None);
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
        if (role is not null && await LfgPoster.WhyUnsafeToSelfAssignAsync(Context.Guild, role) is { } reason)
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

/// <summary>The values submitted from the /settings lfg-game pop-up. The pop-up itself is built in
/// <see cref="SettingsModule.LfgGameAsync"/> (so it can be pre-filled); this only reads it back by the boxes' IDs.</summary>
public class LfgGameModal : IModal
{
    public const string ModalTitle = "LFG game for this server";
    public const string PlaylistsId = "playlists";
    public const string RanksId = "ranks";

    // Discord's limits: labels 45 characters, placeholders 100. Over that, the pop-up refuses to open at all, so a test
    // checks these. The format hint lives in the label, where it always shows.
    public const string PlaylistsLabel = "Modes, one per line (* = ranked)";
    public const string PlaylistsPlaceholder = "Ranked Play* : ranked, comp\nHardpoint : hp\nSearch & Destroy : snd";
    public const string RanksLabel = "Ranks, lowest first, one per line";
    public const string RanksPlaceholder = "Bronze\nSilver\nGold\nPlatinum : plat";

    public string Title => ModalTitle;

    [InputLabel(PlaylistsLabel)]
    [ModalTextInput(PlaylistsId, TextInputStyle.Paragraph, maxLength: LfgGameProfileText.MaxBoxLength)]
    public string? Playlists { get; set; }

    [InputLabel(RanksLabel)]
    [ModalTextInput(RanksId, TextInputStyle.Paragraph, maxLength: LfgGameProfileText.MaxBoxLength)]
    public string? Ranks { get; set; }
}
