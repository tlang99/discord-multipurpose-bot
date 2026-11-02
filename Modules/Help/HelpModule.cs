using Discord;
using Discord.Interactions;
using Discord.Net;
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
    private readonly ChatOptions _chat;
    private readonly ILogger<HelpModule> _logger;

    public HelpModule(IOptions<MusicOptions> music, IOptions<LfgOptions> lfg, IOptions<ChatOptions> chat,
        ILogger<HelpModule> logger)
    {
        _music = music.Value;
        _lfg = lfg.Value;
        _chat = chat.Value;
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

        // Manage Server alone shouldn't let someone use the bot to post and pin in a channel they couldn't post and
        // pin in themselves, like a read-only #announcements.
        if (Context.User is not IGuildUser user ||
            user.GetPermissions(channel) is not { SendMessages: true, PinMessages: true })
        {
            await FollowupAsync("You need **Send Messages** and **Pin Messages** in this channel to post the command " +
                               "list here.", ephemeral: true);
            return;
        }

        var message = await channel.SendMessageAsync(embed: BuildEmbed(), allowedMentions: AllowedMentions.None);

        var pinned = false;
        try
        {
            await message.PinAsync();
            pinned = true;
        }
        catch (HttpException ex)
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

        var builder = new EmbedBuilder()
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
                "Controls only work from the voice channel the bot is in.\n" +
                "`/session open` - a music session: everyone adds songs, then they play in turns, with vote skip. " +
                "Use the buttons on its menu, or `/session start` · `/session end` · `/session leave` · `/session menu`.")
            .AddField("⏰ Reminders",
                "`/remind` - e.g. `when: in 2h` `what: start the raid`, and I'll ping you in that channel\n" +
                "`/reminders list` · `/reminders cancel` - see or cancel yours");

        if (_chat.IsEnabled)
        {
            builder.AddField("💬 Chat with me",
                $"Mention {Context.Guild.CurrentUser.Mention} in a message, or reply to one of mine, and I'll answer. " +
                "I read the last few messages in the channel for context." +
                (_chat.Actions
                    ? "\nYou can also just ask: \"play never gonna give you up\", \"skip this\", \"show the queue\", " +
                      "\"start a music session\", \"make an lfg for ranked, need 3 more, mic required\" or \"remind me " +
                      "in 20 minutes to check the oven\"."
                    : ""));
        }

        return builder.AddField("ℹ️ Help", "`/help` - show this list").Build();
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
