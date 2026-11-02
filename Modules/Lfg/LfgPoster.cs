using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Lfg;

namespace ModBot.Modules.Lfg;

/// <summary>What happened when posting an LFG: a message for the host, and the card's link when it was posted.</summary>
public record LfgPostResult(bool Success, string Message, string? CardLink = null);

/// <summary>
/// Posts an LFG card with its role ping, shared by /lfg and the chatbot ("make an LFG for ranked") so both follow the
/// same rules: one open post per person, the ping cooldown, and the bot's permissions in the channel. Discord-facing
/// glue around <see cref="ILfgService"/>, which holds the rules themselves.
/// </summary>
public class LfgPoster
{
    public const int MaxNoteLength = 100;

    private readonly ILfgService _lfg;
    private readonly LfgOptions _options;
    private readonly ILogger<LfgPoster> _logger;

    public LfgPoster(ILfgService lfg, IOptions<LfgOptions> options, ILogger<LfgPoster> logger)
    {
        _lfg = lfg;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The configured LFG channel's name, or empty when LFG posts can go anywhere.</summary>
    public string ChannelName => ConfiguredChannels.ChannelName(_options.ChannelName);

    public string PingRoleName => ConfiguredChannels.RoleName(_options.PingRoleName);

    public TimeSpan PingCooldown => TimeSpan.FromMinutes(Math.Max(0, _options.PingCooldownMinutes));

    public TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Clamp(_options.ExpiryMinutes, 5, 24 * 60));

    /// <summary>The configured LFG channel, or null if the server doesn't have it.</summary>
    public ITextChannel? FindChannel(SocketGuild guild) => ConfiguredChannels.Find(guild, ChannelName);

    public IRole? FindPingRole(SocketGuild guild) => ConfiguredChannels.FindRole(guild, PingRoleName);

    /// <summary>Posts the card in <paramref name="channel"/> for <paramref name="host"/>, pinging the LFG role if
    /// allowed. The channel is the caller's decision; this only checks the bot can post there.</summary>
    public async Task<LfgPostResult> PostAsync(SocketGuildUser host, ITextChannel channel, int players,
        LfgDetails details, string? note)
    {
        var guild = host.Guild;
        if (ChannelAccess.WhyBotCantPostEmbeds(guild.CurrentUser, channel, "LFGs") is { } problem)
        {
            return new(false, problem);
        }

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            note = note[..MaxNoteLength];
        }

        var lifetime = Lifetime;
        var (post, created) = _lfg.Create(guild.Id, channel.Id, host.Id, LfgMode.Multiplayer, players, details, note,
            lifetime);

        if (!created)
        {
            var where = post.MessageId == 0 ? "already" : $"already ([here]({CardLink(post)}))";
            return new(false, $"You {where} have an open LFG post. Start or cancel that one first.");
        }

        // Ping the opt-in LFG role, if the server has one - at most once per cooldown per host. When it doesn't ping,
        // say why in the host's confirmation instead of skipping silently.
        var pingRole = FindPingRole(guild);
        string? pingText = null;
        var pingMentions = AllowedMentions.None;
        string? pingStatus = null;
        if (pingRole is null)
        {
            pingStatus = PingRoleName.Length == 0
                ? null
                : "No @LFG ping: pings aren't set up yet - a server admin can run `/lfg-setup`.";
        }
        else if (!pingRole.IsMentionable && !guild.CurrentUser.GuildPermissions.MentionEveryone)
        {
            _logger.LogWarning("Can't ping LFG role {RoleId} in guild {GuildId}: it isn't mentionable and the bot lacks " +
                               "Mention All Roles. Running /lfg-setup again fixes it.", pingRole.Id, guild.Id);
            pingStatus = $"No @{pingRole.Name} ping: that role isn't mentionable. A server admin can run `/lfg-setup` " +
                         "again to fix it.";
        }
        else if (!_lfg.TryClaimRolePing(guild.Id, host.Id, PingCooldown))
        {
            pingStatus = $"No @{pingRole.Name} ping this time: your last post pinged it less than " +
                         $"{(int)PingCooldown.TotalMinutes} minutes ago.";
        }
        else
        {
            (pingText, pingMentions) = LfgMessages.RolePing(post, pingRole.Id);
            pingStatus = host.Roles.Any(r => r.Id == pingRole.Id)
                ? $"Pinged @{pingRole.Name} (Discord doesn't notify you about your own post, but everyone else with the role is)."
                : $"Pinged @{pingRole.Name}.";
        }

        // Posted as a regular message rather than an interaction reply: interaction replies can only be edited for 15
        // minutes, and the post needs updating until it expires an hour later.
        try
        {
            var message = await channel.SendMessageAsync(text: pingText, embed: LfgMessages.OpenEmbed(post),
                components: LfgMessages.OpenButtons(post), allowedMentions: pingMentions);
            _lfg.AttachMessage(post.Id, message.Id);
            post.MessageId = message.Id;
        }
        catch (Exception ex)
        {
            _lfg.Discard(post.Id);
            if (pingText is not null)
            {
                // Nobody was pinged, so don't make the host wait out the cooldown.
                _lfg.ReleaseRolePing(guild.Id, host.Id);
            }

            _logger.LogWarning(ex, "Couldn't post an LFG in channel {ChannelId}.", channel.Id);
            return new(false, $"Couldn't post your LFG in {channel.Mention}.");
        }

        return new(true,
            $"Your LFG is up. It closes when the squad is full, or after {(int)lifetime.TotalMinutes} minutes. Use " +
            "**Start now** to go early with whoever's joined." + (pingStatus is null ? "" : $"\n{pingStatus}"),
            CardLink(post));
    }

    private static string CardLink(LfgPost post) => MessageTools.Link(post.GuildId, post.ChannelId, post.MessageId);
}
