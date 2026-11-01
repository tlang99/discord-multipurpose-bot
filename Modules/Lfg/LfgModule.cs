using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Lfg;

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
    private readonly ILogger<LfgModule> _logger;

    public LfgModule(ILfgService lfg, LfgPoster poster, ILogger<LfgModule> logger)
    {
        _lfg = lfg;
        _poster = poster;
        _logger = logger;
    }

    // Slash-command choices. Kept here rather than on the Models enums so Models stays free of Discord.Net; mapped by
    // ToDetails below.
    public enum PlaylistChoice
    {
        [ChoiceDisplay("Any playlist")] Any,
        [ChoiceDisplay("Ranked Play")] RankedPlay,
        [ChoiceDisplay("Hardpoint")] Hardpoint,
        [ChoiceDisplay("Search & Destroy")] SearchAndDestroy,
        [ChoiceDisplay("Team Deathmatch")] TeamDeathmatch,
        [ChoiceDisplay("Domination")] Domination,
        [ChoiceDisplay("Kill Confirmed")] KillConfirmed,
        [ChoiceDisplay("Hardcore")] Hardcore,
    }

    public enum RankChoice
    {
        [ChoiceDisplay("Bronze+")] Bronze,
        [ChoiceDisplay("Silver+")] Silver,
        [ChoiceDisplay("Gold+")] Gold,
        [ChoiceDisplay("Platinum+")] Platinum,
        [ChoiceDisplay("Diamond+")] Diamond,
        [ChoiceDisplay("Crimson+")] Crimson,
        [ChoiceDisplay("Iridescent+")] Iridescent,
        [ChoiceDisplay("Top 250")] Top250,
    }

    public enum PlatformChoice
    {
        [ChoiceDisplay("Crossplay (anyone)")] Crossplay,
        [ChoiceDisplay("PC only")] PcOnly,
        [ChoiceDisplay("Console only")] ConsoleOnly,
    }

    [SlashCommand("lfg", "Looking for a group? Post it here and let people join.")]
    public async Task LfgAsync(
        [Summary(description: "Total squad size, including you")] [MinValue(2)] [MaxValue(6)] int players = 4,
        [Summary(description: "Which playlist")] PlaylistChoice playlist = PlaylistChoice.Any,
        [Summary(description: "Minimum rank (Ranked Play)")] RankChoice? rank = null,
        [Summary(description: "Do people need a mic?")] bool? mic = null,
        [Summary(description: "Who can join")] PlatformChoice platform = PlatformChoice.Crossplay,
        [Summary(description: "Anything else, e.g. \"chill games, no sweats\"")] [MaxLength(MaxNoteLength)] string? note = null)
    {
        // Acknowledge first (privately): posting the card can take long enough to miss Discord's 3-second window.
        await DeferAsync(ephemeral: true);

        // A rank only means something in Ranked Play: picking one implies it, but not alongside another playlist.
        if (rank is not null && playlist is not (PlaylistChoice.Any or PlaylistChoice.RankedPlay))
        {
            await FollowupAsync("A minimum rank only applies to **Ranked Play** - pick that playlist, or leave rank empty.",
                ephemeral: true);
            return;
        }

        if (rank is not null)
        {
            playlist = PlaylistChoice.RankedPlay;
        }

        if (Context.Channel is not ITextChannel channel)
        {
            await FollowupAsync("LFG posts can only go in text channels.", ephemeral: true);
            return;
        }

        var lfgChannelName = _poster.ChannelName;
        if (lfgChannelName.Length > 0 && !channel.Name.Equals(lfgChannelName, StringComparison.OrdinalIgnoreCase))
        {
            var lfgChannel = Context.Guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(lfgChannelName, StringComparison.OrdinalIgnoreCase));
            await FollowupAsync(lfgChannel is null
                    ? $"LFG posts go in **#{lfgChannelName}**, but this server doesn't have that channel yet."
                    : $"LFG posts go in {lfgChannel.Mention} - run `/lfg` there.",
                ephemeral: true);
            return;
        }

        if (Context.User is not SocketGuildUser host)
        {
            await FollowupAsync("Couldn't work out who you are in this server - try again.", ephemeral: true);
            return;
        }

        var result = await _poster.PostAsync(host, channel, players, ToDetails(playlist, rank, mic, platform), note);
        await FollowupAsync(result.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("lfg-setup", "Posts the button panel members use to opt in to LFG pings (creates the role if needed).")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task LfgSetupAsync()
    {
        // Creating the role, posting and pinning the panel easily takes longer than Discord's 3-second window.
        await DeferAsync(ephemeral: true);

        var roleName = PingRoleName;
        if (roleName.Length == 0)
        {
            await FollowupAsync("LFG pings are turned off (`Lfg:PingRoleName` is empty).", ephemeral: true);
            return;
        }

        var bot = Context.Guild.CurrentUser;
        if (!bot.GuildPermissions.ManageRoles)
        {
            await FollowupAsync("I need the **Manage Roles** permission to create and hand out the LFG ping role.",
                ephemeral: true);
            return;
        }

        if (Context.Channel is not ITextChannel channel ||
            bot.GetPermissions(channel) is not { SendMessages: true, EmbedLinks: true })
        {
            await FollowupAsync("I need **Send Messages** and **Embed Links** here to post the panel.", ephemeral: true);
            return;
        }

        // Manage Server alone shouldn't let someone use the bot to post and pin in a channel they couldn't post and
        // pin in themselves. Checked before touching the role so a refused run changes nothing.
        if (Context.User is not IGuildUser user ||
            user.GetPermissions(channel) is not { SendMessages: true, PinMessages: true })
        {
            await FollowupAsync("You need **Send Messages** and **Pin Messages** in this channel to post the LFG ping " +
                               "panel here.", ephemeral: true);
            return;
        }

        var role = FindPingRole();
        var createdRole = role is null;
        if (createdRole)
        {
            // Mentionable, so the bot can ping it without needing the broader "Mention All Roles" permission. No
            // permissions, since anyone can give it to themselves from the panel.
            role = await Context.Guild.CreateRoleAsync(roleName, GuildPermissions.None, color: null, isHoisted: false,
                isMentionable: true);
        }
        else if (WhyUnsafeToSelfAssign(role!) is { } reason)
        {
            await FollowupAsync($"There's already a **@{Format.Sanitize(role!.Name)}** role, but it {reason}. Members " +
                               "can give the ping role to themselves, so it must be a plain role with no permissions. " +
                               "Remove those permissions, or rename that role and run this again to create a fresh one.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        if (role!.Position >= bot.Hierarchy)
        {
            await FollowupAsync($"The **@{Format.Sanitize(role.Name)}** role is above my highest role, so I can't hand it " +
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

        var pinned = false;
        try
        {
            await panel.PinAsync();
            pinned = true;
        }
        catch (HttpException ex)
        {
            _logger.LogInformation(ex, "Couldn't pin the LFG ping panel in channel {ChannelId}.", channel.Id);
        }

        await FollowupAsync((createdRole ? $"Created the **@{Format.Sanitize(role.Name)}** role. " : "") +
                           (madeMentionable ? $"Made **@{Format.Sanitize(role.Name)}** mentionable so pings notify people. " : "") +
                           $"Posted the opt-in panel{(pinned ? " and pinned it" : " (I couldn't pin it - pin it yourself if you like)")}. " +
                           $"New `/lfg` posts will ping the role, at most once every {(int)PingCooldown.TotalMinutes} " +
                           "minutes per person.",
            ephemeral: true);
    }

    [ComponentInteraction(LfgMessages.RoleOnId)]
    public Task RoleOnAsync() => SetPingRoleAsync(wanted: true);

    [ComponentInteraction(LfgMessages.RoleOffId)]
    public Task RoleOffAsync() => SetPingRoleAsync(wanted: false);

    private async Task SetPingRoleAsync(bool wanted)
    {
        // Changing roles is an API call that can take a while; acknowledge (privately) before doing it.
        await DeferAsync(ephemeral: true);

        if (FindPingRole() is not { } role || Context.User is not IGuildUser user)
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
        if (wanted && WhyUnsafeToSelfAssign(role) is { } reason)
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

    [ComponentInteraction(LfgMessages.JoinPrefix + ":*")]
    public Task JoinAsync(string postId) => HandleButtonAsync(postId, id => _lfg.Join(id, Context.User.Id));

    [ComponentInteraction(LfgMessages.LeavePrefix + ":*")]
    public Task LeaveAsync(string postId) => HandleButtonAsync(postId, id => _lfg.Leave(id, Context.User.Id));

    [ComponentInteraction(LfgMessages.StartPrefix + ":*")]
    public Task StartAsync(string postId) => HandleButtonAsync(postId, id => _lfg.Start(id, Context.User.Id));

    [ComponentInteraction(LfgMessages.CancelPrefix + ":*")]
    public Task CancelAsync(string postId)
        => HandleButtonAsync(postId, id => _lfg.Cancel(id, Context.User.Id, IsModerator(Context.User as IGuildUser)));

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

    private string PingRoleName => _poster.PingRoleName;

    private TimeSpan PingCooldown => _poster.PingCooldown;

    /// <summary>
    /// Why members must not be able to give themselves this role, or null if it's safe. The panel lets anyone take the
    /// ping role, so it must be harmless: any server permission, or a channel override that grants something (e.g.
    /// access to a hidden channel), would let members escalate their own access just by clicking a button.
    /// </summary>
    private string? WhyUnsafeToSelfAssign(IRole role)
    {
        if (role.IsManaged)
        {
            return "belongs to an integration";
        }

        // Only permissions beyond what @everyone already has count: a manually created role comes with defaults like
        // View Channels, which grant nothing new.
        var extra = new GuildPermissions(role.Permissions.RawValue & ~Context.Guild.EveryoneRole.Permissions.RawValue);
        if (extra.RawValue != 0)
        {
            var names = extra.ToList();
            return $"has server permissions beyond @everyone's ({string.Join(", ", names.Take(3))}" +
                   $"{(names.Count > 3 ? ", ..." : "")})";
        }

        var grantingChannel = Context.Guild.Channels.FirstOrDefault(c =>
            c.GetPermissionOverwrite(role) is { AllowValue: not 0 });
        return grantingChannel is null ? null : $"grants permissions in #{grantingChannel.Name}";
    }

    private IRole? FindPingRole() => _poster.FindPingRole(Context.Guild);

    private static LfgDetails ToDetails(PlaylistChoice playlist, RankChoice? rank, bool? mic, PlatformChoice platform)
        => new(
            Playlist: playlist switch
            {
                PlaylistChoice.RankedPlay => LfgPlaylist.RankedPlay,
                PlaylistChoice.Hardpoint => LfgPlaylist.Hardpoint,
                PlaylistChoice.SearchAndDestroy => LfgPlaylist.SearchAndDestroy,
                PlaylistChoice.TeamDeathmatch => LfgPlaylist.TeamDeathmatch,
                PlaylistChoice.Domination => LfgPlaylist.Domination,
                PlaylistChoice.KillConfirmed => LfgPlaylist.KillConfirmed,
                PlaylistChoice.Hardcore => LfgPlaylist.Hardcore,
                _ => LfgPlaylist.Any,
            },
            MinRank: rank switch
            {
                null => null,
                RankChoice.Silver => LfgRank.Silver,
                RankChoice.Gold => LfgRank.Gold,
                RankChoice.Platinum => LfgRank.Platinum,
                RankChoice.Diamond => LfgRank.Diamond,
                RankChoice.Crimson => LfgRank.Crimson,
                RankChoice.Iridescent => LfgRank.Iridescent,
                RankChoice.Top250 => LfgRank.Top250,
                _ => LfgRank.Bronze,
            },
            MicRequired: mic,
            Platform: platform switch
            {
                PlatformChoice.PcOnly => LfgPlatform.PcOnly,
                PlatformChoice.ConsoleOnly => LfgPlatform.ConsoleOnly,
                _ => LfgPlatform.Crossplay,
            });

    /// <summary>Same moderator bar as the music commands: these members can remove anyone's LFG post.</summary>
    private static bool IsModerator(IGuildUser? user)
        => user is not null && (user.GuildPermissions.ManageMessages || user.GuildPermissions.MoveMembers);
}
