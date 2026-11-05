using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModBot.Common;
using ModBot.Models;
using ModBot.Services.Lfg;
using ModBot.Services.Moderation;
using ModBot.Services.Settings;

namespace ModBot.Modules.Lfg;

/// <summary>
/// "Looking for group" posts: /lfg posts a card with Join/Leave/Start/Cancel buttons in the channel it's run in. When
/// the squad fills up (or the host starts early), everyone in it gets pinged. The rules live in <see cref="ILfgService"/>;
/// this module only turns commands and button clicks into calls to it and renders the result.
/// </summary>
[RequireContext(ContextType.Guild)]
public class LfgModule : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxNoteLength = LfgPoster.MaxNoteLength;

    private readonly ILfgService _lfg;
    private readonly LfgPoster _poster;
    private readonly ServerSettingsService _serverSettings;
    private readonly ILogger<LfgModule> _logger;

    public LfgModule(ILfgService lfg, LfgPoster poster, ServerSettingsService serverSettings, ILogger<LfgModule> logger)
    {
        _lfg = lfg;
        _poster = poster;
        _serverSettings = serverSettings;
        _logger = logger;
    }

    // Kept here rather than on the Models enum so Models stays free of Discord.Net. Playlists and ranks aren't fixed:
    // they're each server's own (/settings lfg-game), offered as suggestions by the handlers below.
    public enum PlatformChoice
    {
        [ChoiceDisplay("Crossplay (anyone)")] Crossplay,
        [ChoiceDisplay("PC only")] PcOnly,
        [ChoiceDisplay("Console only")] ConsoleOnly,
    }

    [RequireFeature(ServerFeature.Lfg)]
    [SlashCommand("lfg", "Looking for a group? Post it here and let people join.")]
    public async Task LfgAsync(
        [Summary(description: "Total squad size, including you")] [MinValue(2)] [MaxValue(6)] int players = 4,
        [Summary(description: "Which playlist or mode (this server's list)")] [Autocomplete(typeof(PlaylistSuggestions))]
        [MaxLength(LfgGameProfileText.MaxNameLength)] string? playlist = null,
        [Summary(description: "Minimum rank (this server's list)")] [Autocomplete(typeof(RankSuggestions))]
        [MaxLength(LfgGameProfileText.MaxNameLength)] string? rank = null,
        [Summary(description: "Do people need a mic?")] bool? mic = null,
        [Summary(description: "Who can join")] PlatformChoice platform = PlatformChoice.Crossplay,
        [Summary(description: "Anything else, e.g. \"chill games, no sweats\"")] [MaxLength(MaxNoteLength)] string? note = null)
    {
        // Acknowledge first (privately): posting the card can take long enough to miss Discord's 3-second window.
        await DeferAsync(ephemeral: true);

        // Suggestions are only suggestions: a typed value has to be one of this server's names or other names.
        var game = await _serverSettings.LfgGameAsync(Context.Guild.Id);
        var (chosenPlaylist, playlistProblem) = Pick(playlist, game.Playlists, "playlist");
        var (chosenRank, rankProblem) = Pick(rank, game.Ranks, "rank");
        if ((playlistProblem ?? rankProblem) is { } unknown)
        {
            await FollowupAsync(unknown, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        // A rank only means something in a ranked playlist: picking one implies it, but not alongside another one.
        var choice = game.Combine(chosenPlaylist, chosenRank);
        if (choice.Problem is { } problem)
        {
            await FollowupAsync(problem, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (Context.Channel is not ITextChannel channel)
        {
            await FollowupAsync("LFG posts can only go in text channels.", ephemeral: true);
            return;
        }

        var lfgChannel = await _serverSettings.LfgChannelAsync(Context.Guild);
        if (!lfgChannel.Allows(channel))
        {
            await FollowupAsync(lfgChannel.GoThere("LFG posts go", "`/lfg`"), ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser host)
        {
            await FollowupAsync("Couldn't work out who you are in this server - try again.", ephemeral: true);
            return;
        }

        var details = LfgDetails.From(game, choice, mic, platform switch
        {
            PlatformChoice.PcOnly => LfgPlatform.PcOnly,
            PlatformChoice.ConsoleOnly => LfgPlatform.ConsoleOnly,
            _ => LfgPlatform.Crossplay,
        });
        var result = await _poster.PostAsync(host, channel, players, details, note);
        await FollowupAsync(result.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    /// <summary>"Ranked isn't one of this server's playlists" plus what is, or how to set them up when there are none.</summary>
    private static string Unknown(string what, string typed, IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0
            ? $"This server hasn't set up any {what}s for LFG yet - a server admin can add them with " +
              "`/settings lfg-game`. Leave it empty to post without one."
            : $"`{typed.Trim().Replace("`", "")}` isn't one of this server's {what}s. Pick one of: " +
              $"{string.Join(", ", list.Select(n => $"**{TextTools.Escape(n)}**"))}.";
    }

    /// <summary>The playlist or rank a typed value names (by name or other name), or, if it names none, what to tell
    /// the person. Nothing typed is fine: no playlist or rank.</summary>
    private static (T? Found, string? Problem) Pick<T>(string? typed, IReadOnlyList<T> options, string what)
        where T : LfgNamedOption
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return (null, null);
        }

        return options.FirstOrDefault(o => o.IsNamed(typed)) is { } found
            ? (found, null)
            : (null, Unknown(what, typed, options.Select(o => o.Name)));
    }

    /// <summary>Suggests the server's playlists as the person types /lfg playlist.</summary>
    public sealed class PlaylistSuggestions : LfgSuggestions
    {
        protected override IEnumerable<LfgNamedOption> Options(LfgGameProfile game) => game.Playlists;
    }

    /// <summary>Suggests the server's ranks as the person types /lfg rank.</summary>
    public sealed class RankSuggestions : LfgSuggestions
    {
        protected override IEnumerable<LfgNamedOption> Options(LfgGameProfile game) => game.Ranks;
    }

    /// <summary>
    /// Suggests names from the server's LFG game matching what's been typed so far (in the name or one of its other
    /// names), in the server's order; Discord shows at most 25, which is also the most a server can set. Nothing is
    /// suggested outside a server or while the server has LFG turned off, the same places /lfg itself won't run.
    /// </summary>
    public abstract class LfgSuggestions : AutocompleteHandler
    {
        protected abstract IEnumerable<LfgNamedOption> Options(LfgGameProfile game);

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
        {
            var settings = services.GetRequiredService<ServerSettingsService>();
            if (context.Guild is not { } guild || !await settings.IsEnabledAsync(guild.Id, ServerFeature.Lfg))
            {
                return AutocompletionResult.FromSuccess();
            }

            var typed = autocompleteInteraction.Data.Current.Value as string ?? "";
            var matches = Options(await settings.LfgGameAsync(guild.Id))
                .Where(o => o.Matches(typed))
                .Take(LfgGameProfileText.MaxEntries)
                .Select(o => new AutocompleteResult(o.Name, o.Name));
            return AutocompletionResult.FromSuccess(matches);
        }
    }

    [RequireFeature(ServerFeature.Lfg)]
    [SlashCommand("lfg-setup", "Posts the button panel members use to opt in to LFG pings (creates the role if needed).")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task LfgSetupAsync()
    {
        // Creating the role, posting and pinning the panel easily takes longer than Discord's 3-second window.
        await DeferAsync(ephemeral: true);

        var ping = await _serverSettings.LfgPingRoleAsync(Context.Guild);
        if (ping.Off)
        {
            await FollowupAsync("LFG pings are turned off on this server. Turn them on with `/settings lfg-pings`.",
                ephemeral: true);
            return;
        }

        if (ping.Role is null && ping.Name.Length == 0)
        {
            await FollowupAsync("The LFG ping role this server picked has been deleted. Pick another with " +
                               "`/settings lfg-pings`, or leave the role out there to go back to the default.",
                ephemeral: true);
            return;
        }

        var roleName = ping.Name;

        var bot = Context.Guild.CurrentUser;
        if (!bot.GuildPermissions.ManageRoles)
        {
            await FollowupAsync("I need the **Manage Roles** permission to create and hand out the LFG ping role.",
                ephemeral: true);
            return;
        }

        // Checked before touching the role so a refused run changes nothing.
        if (ChannelAccess.WhyCantPostPanel(bot, Context.User as IGuildUser, Context.Channel, "the LFG ping panel") is
            { } problem)
        {
            await FollowupAsync(problem, ephemeral: true);
            return;
        }

        var channel = (ITextChannel)Context.Channel;

        var role = ping.Role;
        var createdRole = role is null;
        if (createdRole)
        {
            // Mentionable, so the bot can ping it without needing the broader "Mention All Roles" permission. No
            // permissions, since anyone can give it to themselves from the panel.
            role = await Context.Guild.CreateRoleAsync(roleName, GuildPermissions.None, color: null, isHoisted: false,
                isMentionable: true);
        }
        else if (await LfgPoster.WhyUnsafeToSelfAssignAsync(Context.Guild, role!) is { } reason)
        {
            await FollowupAsync($"There's already a **@{TextTools.Escape(role!.Name)}** role, but it {reason}. Members " +
                               "can give the ping role to themselves, so it must be a plain role with no permissions. " +
                               "Remove those permissions, or rename that role and run this again to create a fresh one.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (role!.Position >= bot.Hierarchy)
        {
            await FollowupAsync($"The **@{TextTools.Escape(role.Name)}** role is above my highest role, so I can't hand it " +
                               "out. Drag my role above it in Server Settings → Roles, then run this again.",
                ephemeral: true);
            return;
        }

        // A role that existed before setup may not be mentionable, which makes the ping text show without notifying
        // anyone (unless the bot has Mention All Roles).
        var madeMentionable = false;
        if (!role.IsMentionable)
        {
            await role.ModifyAsync(r => r.Mentionable = true);
            madeMentionable = true;
        }

        var panel = await channel.SendMessageAsync(embed: LfgMessages.RolePanelEmbed(role.Name),
            components: LfgMessages.RolePanelButtons(), allowedMentions: AllowedMentions.None);

        var pinned = await MessageTools.TryPinAsync(panel, _logger, "the LFG ping panel");

        await FollowupAsync((createdRole ? $"Created the **@{TextTools.Escape(role.Name)}** role. " : "") +
                           (madeMentionable ? $"Made **@{TextTools.Escape(role.Name)}** mentionable so pings notify people. " : "") +
                           $"Posted the opt-in panel{(pinned ? " and pinned it" : " (I couldn't pin it - pin it yourself if you like)")}. " +
                           "New `/lfg` posts will ping the role, at most once every " +
                           $"{(int)_poster.PingCooldown.TotalMinutes} minutes per person.",
            ephemeral: true);
    }

    [RequireFeature(ServerFeature.Lfg)]
    [ComponentInteraction(LfgMessages.RoleOnId)]
    public Task RoleOnAsync() => SetPingRoleAsync(wanted: true);

    [ComponentInteraction(LfgMessages.RoleOffId)]
    public Task RoleOffAsync() => SetPingRoleAsync(wanted: false);

    private async Task SetPingRoleAsync(bool wanted)
    {
        // Changing roles is an API call that can take a while; acknowledge (privately) before doing it.
        await DeferAsync(ephemeral: true);

        if ((await _serverSettings.LfgPingRoleAsync(Context.Guild)).Role is not { } role ||
            Context.User is not IGuildUser user)
        {
            await FollowupAsync("LFG pings aren't set up right now - ask a server admin to run `/lfg-setup`.",
                ephemeral: true);
            return;
        }

        var has = user.RoleIds.Contains(role.Id);
        if (has == wanted)
        {
            await FollowupAsync(wanted ? "You already get LFG pings." : "You already don't get LFG pings.", ephemeral: true);
            return;
        }

        // Checked on every click, not just at setup: the role is found by name, and it could have gained permissions
        // (or been swapped for a renamed role) since. Removing it is always allowed.
        if (wanted && await LfgPoster.WhyUnsafeToSelfAssignAsync(Context.Guild, role) is { } reason)
        {
            _logger.LogWarning("Refused to self-assign LFG role {RoleId} in guild {GuildId}: it {Reason}.",
                role.Id, Context.Guild.Id, reason);
            await FollowupAsync("LFG pings are switched off right now because the ping role was given extra permissions. " +
                               "A server admin needs to fix the role and run `/lfg-setup` again.", ephemeral: true);
            return;
        }

        try
        {
            if (wanted)
            {
                await user.AddRoleAsync(role);
            }
            else
            {
                await user.RemoveRoleAsync(role);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't {Action} the LFG ping role for user {UserId}.", wanted ? "add" : "remove",
                user.Id);
            await FollowupAsync("I couldn't change your roles - ask a server admin to check my **Manage Roles** " +
                               "permission and that my role is above the LFG role.", ephemeral: true);
            return;
        }

        await FollowupAsync(wanted
            ? $"🔔 You'll be pinged for new LFG posts. Click **Stop LFG pings** to turn it off."
            : "🔕 You won't be pinged for LFG posts anymore.", ephemeral: true);
    }

    [RequireFeature(ServerFeature.Lfg)]
    [ComponentInteraction(LfgMessages.JoinPrefix + ":*")]
    public Task JoinAsync(string postId) => HandleButtonAsync(postId, id => _lfg.Join(id, Context.User.Id));

    [ComponentInteraction(LfgMessages.LeavePrefix + ":*")]
    public Task LeaveAsync(string postId) => HandleButtonAsync(postId, id => _lfg.Leave(id, Context.User.Id));

    [RequireFeature(ServerFeature.Lfg)]
    [ComponentInteraction(LfgMessages.StartPrefix + ":*")]
    public Task StartAsync(string postId) => HandleButtonAsync(postId, id => _lfg.Start(id, Context.User.Id));

    [ComponentInteraction(LfgMessages.CancelPrefix + ":*")]
    public Task CancelAsync(string postId)
        => HandleButtonAsync(postId,
            id => _lfg.Cancel(id, Context.User.Id, Moderators.IsModerator(Context.User as IGuildUser)));

    private async Task HandleButtonAsync(string rawPostId, Func<Guid, LfgResult> action)
    {
        var interaction = (SocketMessageComponent)Context.Interaction;
        var result = Guid.TryParseExact(rawPostId, "N", out var postId)
            ? action(postId)
            : new LfgResult(LfgOutcome.NotFound, null);

        switch (result.Outcome)
        {
            case LfgOutcome.Updated:
                await interaction.UpdateAsync(m =>
                {
                    m.Embed = LfgMessages.OpenEmbed(result.Post!);
                    m.Components = LfgMessages.OpenButtons(result.Post!);
                });
                break;

            case LfgOutcome.SquadReady:
                var full = result.Post!.OpenSpots == 0;
                await interaction.UpdateAsync(m =>
                {
                    m.Embed = LfgMessages.ReadyEmbed(result.Post, full);
                    m.Components = new ComponentBuilder().Build();
                });
                var (text, mentions) = LfgMessages.ReadyPing(result.Post, full);
                await FollowupAsync(text, allowedMentions: mentions);
                break;

            case LfgOutcome.Cancelled:
                var byModerator = result.Post!.HostId != Context.User.Id;
                await interaction.UpdateAsync(m =>
                {
                    m.Embed = LfgMessages.CancelledEmbed(result.Post, byModerator);
                    m.Components = new ComponentBuilder().Build();
                });
                break;

            case LfgOutcome.AlreadyClosed:
                // e.g. clicked Join just after the last spot went. The message already shows the final state; editing
                // it here would put back the stale card this click came from.
                await RespondAsync("Too late - that LFG just closed. Start a new one with `/lfg`.", ephemeral: true);
                break;

            case LfgOutcome.NotFound:
                // From before the bot restarted: make the old post stop offering buttons.
                await interaction.UpdateAsync(m =>
                {
                    m.Embed = LfgMessages.InactiveEmbed(interaction.Message.Embeds.FirstOrDefault());
                    m.Components = new ComponentBuilder().Build();
                });
                await FollowupAsync("That LFG post isn't active anymore - start a new one with `/lfg`.", ephemeral: true);
                break;

            default:
                await RespondAsync(result.Outcome switch
                {
                    LfgOutcome.AlreadyJoined => "You're already in this squad.",
                    LfgOutcome.NotJoined => "You're not in this squad.",
                    LfgOutcome.Full => "This squad is already full.",
                    LfgOutcome.NotHost => "Only the person who posted this LFG can do that.",
                    LfgOutcome.HostCantLeave => "You're the host - use **Cancel** to close your LFG instead.",
                    LfgOutcome.NeedsMorePlayers => "Nobody has joined yet - wait for at least one more player, or **Cancel**.",
                    _ => "That didn't work.",
                }, ephemeral: true);
                break;
        }
    }
}
