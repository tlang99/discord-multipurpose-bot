using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Configuration;

namespace ModBot.Modules.Help;

/// <summary>
/// Lists the commands regular members can use. /help shows it privately to whoever asks; /help-post posts it in the
/// current channel and pins it, so it can live somewhere permanent like #rules or #bot-commands. Moderator and admin
/// commands are left out on purpose: members can't run them, and Discord already shows them to the people who can.
/// </summary>
[RequireContext(ContextType.Guild)]
public class HelpModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly MusicOptions _music;
    private readonly LfgOptions _lfg;
    private readonly ILogger<HelpModule> _logger;

    public HelpModule(IOptions<MusicOptions> music, IOptions<LfgOptions> lfg, ILogger<HelpModule> logger)
    {
        _music = music.Value;
        _lfg = lfg.Value;
        _logger = logger;
    }

    [SlashCommand("help", "Shows the commands you can use in this server.")]
    public Task HelpAsync() => RespondAsync(embed: BuildEmbed(), ephemeral: true);

    [SlashCommand("help-post", "Posts the member command list in this channel and pins it.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public async Task HelpPostAsync()
    {
        await DeferAsync(ephemeral: true);

        if (Context.Channel is not ITextChannel channel ||
            Context.Guild.CurrentUser.GetPermissions(channel) is not { SendMessages: true, EmbedLinks: true })
        {
            await FollowupAsync("I need **Send Messages** and **Embed Links** here to post the command list.",
                ephemeral: true);
            return;
        }

        var message = await channel.SendMessageAsync(embed: BuildEmbed(), allowedMentions: AllowedMentions.None);

        var pinned = false;
        try
        {
            await message.PinAsync();
            pinned = true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't pin the command list in channel {ChannelId}.", channel.Id);
        }

        await FollowupAsync(pinned
                ? "Posted the command list and pinned it. If commands change, delete it and run this again."
                : "Posted the command list (I couldn't pin it - pin it yourself if you like, or give me **Pin Messages**).",
            ephemeral: true);
    }

    private Embed BuildEmbed()
    {
        var musicWhere = ChannelText(_music.ChannelName);
        var lfgWhere = ChannelText(_lfg.ChannelName);

        return new EmbedBuilder()
            .WithTitle("Bot commands")
            .WithDescription("Type `/` in the chat box to see these with their options. Use `/help` anytime to see " +
                             "this list (only you will see it).")
            .WithColor(new Color(0x5865F2))
            .AddField("🎮 Looking for group" + (lfgWhere is null ? "" : $" - in {lfgWhere}"),
                "`/lfg` - post a squad card others can join. All options are optional: `players`, `playlist`, " +
                "`rank`, `mic`, `platform`, `note`.\n" +
                "Use the **Join** / **Leave** buttons on someone's card, or **Start now** / **Cancel** on yours.\n" +
                "Want a ping when someone posts? Click **Get LFG pings** on the pinned panel.")
            .AddField("🎵 Music" + (musicWhere is null ? "" : $" - in {musicWhere}"),
                "Join a voice channel first.\n" +
                "`/play` - play a song name or YouTube link (or add it to the queue)\n" +
                "`/pause` · `/resume` · `/skip` · `/stop`\n" +
                "`/nowplaying` · `/queue` - see what's playing and what's next\n" +
                "`/remove` · `/move` · `/shuffle` · `/loop` · `/clearqueue` - manage the queue\n" +
                "Controls only work from the voice channel the bot is in.")
            .AddField("ℹ️ Help", "`/help` - show this list")
            .Build();
    }

    /// <summary>A clickable mention of the named channel if it exists, its plain name if not, or null when the feature
    /// isn't limited to one channel.</summary>
    private string? ChannelText(string configuredName)
    {
        var name = configuredName.Trim().TrimStart('#');
        if (name.Length == 0)
            return null;

        var channel = Context.Guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return channel?.Mention ?? $"#{name}";
    }
}
