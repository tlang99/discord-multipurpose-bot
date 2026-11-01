using Discord;
using Discord.Interactions;
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
    private const int MaxNoteLength = 100;

    private readonly ILfgService _lfg;
    private readonly LfgOptions _options;
    private readonly ILogger<LfgModule> _logger;

    public LfgModule(ILfgService lfg, IOptions<LfgOptions> options, ILogger<LfgModule> logger)
    {
        _lfg = lfg;
        _options = options.Value;
        _logger = logger;
    }

    [SlashCommand("lfg", "Looking for a group? Post it here and let people join.")]
    public async Task LfgAsync(
        [Summary(description: "Total squad size, including you")] [MinValue(2)] [MaxValue(6)] int players = 4,
        [Summary(description: "Anything else, e.g. \"ranked, mics on\"")] [MaxLength(MaxNoteLength)] string? note = null)
    {
        if (Context.Channel is not ITextChannel channel)
        {
            await RespondAsync("LFG posts can only go in text channels.", ephemeral: true);
            return;
        }

        var lfgChannelName = _options.ChannelName.Trim().TrimStart('#');
        if (lfgChannelName.Length > 0 && !channel.Name.Equals(lfgChannelName, StringComparison.OrdinalIgnoreCase))
        {
            var lfgChannel = Context.Guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(lfgChannelName, StringComparison.OrdinalIgnoreCase));
            await RespondAsync(lfgChannel is null
                    ? $"LFG posts go in **#{lfgChannelName}**, but this server doesn't have that channel yet."
                    : $"LFG posts go in {lfgChannel.Mention} - run `/lfg` there.",
                ephemeral: true);
            return;
        }

        var botPermissions = Context.Guild.CurrentUser.GetPermissions(channel);
        if (!botPermissions.ViewChannel || !botPermissions.SendMessages || !botPermissions.EmbedLinks)
        {
            await RespondAsync($"I need **View Channel**, **Send Messages**, and **Embed Links** in {channel.Mention} " +
                               "to post LFGs here.", ephemeral: true);
            return;
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(_options.ExpiryMinutes, 5, 24 * 60));
        var (post, created) = _lfg.Create(Context.Guild.Id, channel.Id, Context.User.Id, LfgMode.Multiplayer,
            players, note, lifetime);

        if (!created)
        {
            var where = post.MessageId == 0
                ? "already"
                : $"already ([here](https://discord.com/channels/{post.GuildId}/{post.ChannelId}/{post.MessageId}))";
            await RespondAsync($"You {where} have an open LFG post. Start or cancel that one first.", ephemeral: true);
            return;
        }

        // Posted as a regular message rather than the command's reply: interaction replies can only be edited for 15
        // minutes, and the post needs updating until it expires an hour later.
        try
        {
            var message = await channel.SendMessageAsync(embed: LfgMessages.OpenEmbed(post),
                components: LfgMessages.OpenButtons(post), allowedMentions: AllowedMentions.None);
            _lfg.AttachMessage(post.Id, message.Id);
        }
        catch (Exception ex)
        {
            _lfg.Discard(post.Id);
            _logger.LogWarning(ex, "Couldn't post an LFG in channel {ChannelId}.", channel.Id);
            await RespondAsync("Couldn't post your LFG in this channel.", ephemeral: true);
            return;
        }

        await RespondAsync($"Your LFG is up. It closes when the squad is full, or after " +
                           $"{(int)lifetime.TotalMinutes} minutes. Use **Start now** to go early with whoever's joined.",
            ephemeral: true);
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

    /// <summary>Same moderator bar as the music commands: these members can remove anyone's LFG post.</summary>
    private static bool IsModerator(IGuildUser? user)
        => user is not null && (user.GuildPermissions.ManageMessages || user.GuildPermissions.MoveMembers);
}
