using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
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

        if (ChannelAccess.WhyCantPostPanel(Context.Guild.CurrentUser, Context.User as IGuildUser, Context.Channel,
                "the command list") is { } problem)
        {
            await FollowupAsync(problem, ephemeral: true);
            return;
        }

        var channel = (ITextChannel)Context.Channel;
        var message = await channel.SendMessageAsync(embed: BuildEmbed(), allowedMentions: AllowedMentions.None);
        var pinned = await MessageTools.TryPinAsync(message, _logger, "the command list");

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
            .WithColor(Colors.Bot)
            .AddField("🎮 Looking for group" + (lfgWhere is null ? "" : $" - in {lfgWhere}"),
                "`/lfg` - post a squad card others can join. All options are optional: `players`, `playlist`, " +
                "`rank`, `mic`, `platform`, `note`.\n" +
                "Use the **Join** / **Leave** buttons on someone's card, or **Start now** / **Cancel** on yours.\n" +
                "Want a ping when someone posts? Click **Get LFG pings** on the pinned panel.")
            .AddField("🎵 Music" + (musicWhere is null ? "" : $" - in {musicWhere}"),
                "One queue for everyone, kept between listening sessions. Songs play in turns: one from each person.\n" +
                "`/add` - add a song name or YouTube link (any time, from anywhere)\n" +
                "`/play` - add it and start the music in your voice channel · `/start` - play the queue\n" +
                "`/skip` - vote to skip (more than half the listeners; instant if it's your pick)\n" +
                "`/pause` · `/resume` · `/stop` (the queue's kept) · `/loop`\n" +
                "`/nowplaying` · `/queue` · `/remove` · `/remove-mine`\n" +
                "Or use the buttons on the pinned music menu. Controlling playback works from the bot's voice channel.")
            .AddField("⏰ Reminders",
                "`/remind` - e.g. `when: in 2h` `what: start the raid`, and I'll ping you in that channel\n" +
                "`/reminders list` · `/reminders cancel` - see or cancel yours")
            .AddField("📊 Polls",
                "`/poll` - e.g. `question: what should we play?` `answers: ranked, casual, zombies` (leave out " +
                "`answers` for Yes/No). Optional: `hours` (default 24), `multiple`.\n" +
                "End one early: right-click it → **Apps** → **End poll** (yours, or any if you're a moderator)");

        if (_chat.IsEnabled)
        {
            builder.AddField("💬 Chat with me",
                $"Mention {Context.Guild.CurrentUser.Mention} in a message, or reply to one of mine, and I'll answer. " +
                "I read the last few messages in the channel for context." +
                (_chat.Actions
                    ? "\nYou can also just ask: \"play never gonna give you up\", \"skip this\", \"show the queue\", " +
                      "\"start the music\", \"make an lfg for ranked, need 3 more, mic required\", \"remind me " +
                      "in 20 minutes to check the oven\", \"make a poll: pizza or tacos?\" or \"end my poll\"."
                    : ""));
        }

        return builder.AddField("ℹ️ Help", "`/help` - show this list").Build();
    }

    /// <summary>A clickable mention of the named channel if it exists, its plain name if not, or null when the feature
    /// isn't limited to one channel.</summary>
    private string? ChannelText(string configuredName)
    {
        var name = ConfiguredChannels.ChannelName(configuredName);
        return name.Length == 0 ? null : ConfiguredChannels.Find(Context.Guild, name)?.Mention ?? $"#{name}";
    }
}
