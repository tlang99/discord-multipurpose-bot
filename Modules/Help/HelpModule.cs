using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;
using ModBot.Models;
using ModBot.Services.Settings;

namespace ModBot.Modules.Help;

/// <summary>
/// Lists the commands regular members can use. /help shows it privately to whoever asks; /help-post posts it in the
/// current channel and pins it, so it can live somewhere permanent like #rules or #bot-commands. Moderator and admin
/// commands are left out on purpose: members can't run them, and Discord already shows them to the people who can.
/// </summary>
[RequireContext(ContextType.Guild)]
public class HelpModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ServerSettingsService _settings;
    private readonly ChatOptions _chat;
    private readonly ILogger<HelpModule> _logger;

    public HelpModule(ServerSettingsService settings, IOptions<ChatOptions> chat, ILogger<HelpModule> logger)
    {
        _settings = settings;
        _chat = chat.Value;
        _logger = logger;
    }

    [SlashCommand("help", "Shows the commands you can use in this server.")]
    public async Task HelpAsync() => await RespondAsync(embed: await BuildEmbedAsync(), ephemeral: true);

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
        var message = await channel.SendMessageAsync(embed: await BuildEmbedAsync(),
            allowedMentions: AllowedMentions.None);
        var pinned = await MessageTools.TryPinAsync(message, _logger, "the command list");

        await FollowupAsync(pinned
                ? "Posted the command list and pinned it. If commands change, delete it and run this again."
                : "Posted the command list (I couldn't pin it - pin it yourself if you like, or give me **Pin Messages**).",
            ephemeral: true);
    }

    /// <summary>The member commands for this server: only the features it has on, with its own channels.</summary>
    private async Task<Embed> BuildEmbedAsync()
    {
        var server = await _settings.GetAsync(Context.Guild.Id);

        var builder = new EmbedBuilder()
            .WithTitle("Bot commands")
            .WithDescription("Type `/` in the chat box to see these with their options. Use `/help` anytime to see " +
                             "this list (only you will see it).")
            .WithColor(Colors.Bot);

        if (server.IsEnabled(ServerFeature.Lfg))
        {
            var lfgWhere = (await _settings.LfgChannelAsync(Context.Guild)).Where;
            builder.AddField("🎮 Looking for group" + (lfgWhere is null ? "" : $" - in {lfgWhere}"),
                "`/lfg` - post a squad card others can join. All options are optional: `players`, `playlist`, " +
                "`rank`, `mic`, `platform`, `note`.\n" +
                "Use the **Join** / **Leave** buttons on someone's card, or **Start now** / **Cancel** on yours.\n" +
                "Want a ping when someone posts? Click **Get LFG pings** on the pinned panel.");
        }

        if (server.IsEnabled(ServerFeature.Music))
        {
            var musicWhere = (await _settings.MusicChannelAsync(Context.Guild)).Where;
            builder.AddField("🎵 Music" + (musicWhere is null ? "" : $" - in {musicWhere}"),
                "One queue for everyone, kept between listening sessions. Songs play in turns: one from each person.\n" +
                "`/add` - add a song name or YouTube link (any time, from anywhere)\n" +
                "`/play` - add it and start the music in your voice channel · `/start` - play the queue\n" +
                "`/skip` - vote to skip (more than half the listeners; instant if it's your pick)\n" +
                "`/pause` · `/resume` · `/stop` (the queue's kept) · `/loop`\n" +
                "`/nowplaying` · `/queue` · `/remove` · `/remove-mine`\n" +
                "Or use the buttons on the pinned music menu. Controlling playback works from the bot's voice channel.");
        }

        if (server.IsEnabled(ServerFeature.Reminders))
        {
            builder.AddField("⏰ Reminders",
                "`/remind` - e.g. `when: in 2h` `what: start the raid`, and I'll ping you in that channel\n" +
                "`/reminders list` · `/reminders cancel` - see or cancel yours");
        }

        if (server.IsEnabled(ServerFeature.Polls))
        {
            builder.AddField("📊 Polls",
                "`/poll` - e.g. `question: what should we play?` `answers: ranked, casual, zombies` (leave out " +
                "`answers` for Yes/No). Optional: `hours`, `multiple`.\n" +
                "End one early: right-click it → **Apps** → **End poll** (yours, or any if you're a moderator)");
        }

        if (_chat.IsEnabled && server.IsEnabled(ServerFeature.Chat))
        {
            var examples = new List<string>();
            if (server.IsEnabled(ServerFeature.Music))
            {
                examples.AddRange(["\"play never gonna give you up\"", "\"skip this\"", "\"show the queue\""]);
            }

            if (server.IsEnabled(ServerFeature.Lfg))
            {
                examples.Add("\"make an lfg for ranked, need 3 more, mic required\"");
            }

            if (server.IsEnabled(ServerFeature.Reminders))
            {
                examples.Add("\"remind me in 20 minutes to check the oven\"");
            }

            if (server.IsEnabled(ServerFeature.Polls))
            {
                examples.AddRange(["\"make a poll: pizza or tacos?\"", "\"end my poll\""]);
            }

            var canAct = _chat.Actions && server.IsEnabled(ServerFeature.ChatActions) && examples.Count > 0;
            builder.AddField("💬 Chat with me",
                $"Mention {Context.Guild.CurrentUser.Mention} in a message, or reply to one of mine, and I'll answer. " +
                "I read the last few messages in the channel for context." +
                (canAct ? $"\nYou can also just ask: {string.Join(", ", examples)}." : ""));

            if (server.IsEnabled(ServerFeature.VoiceCommands))
            {
                var name = Context.Guild.CurrentUser.DisplayName;
                builder.AddField("🎙️ Voice commands",
                    (server.IsEnabled(ServerFeature.VoiceAutoJoin)
                        ? "I join voice by myself when people are in a voice channel. "
                        : "In a voice channel, `/voice listen` (or ask me to \"listen in voice\"). ") +
                    $"Say \"hey {name}\" and what you want: \"hey {name}, make an LFG for ranked\". I do it as if " +
                    "you'd typed it, and post what I heard. Speech is turned into text on the bot's own computer and " +
                    $"no audio is kept. `/voice stop` or \"hey {name}, stop listening\" to stop. Not while music's " +
                    "playing.");
            }
        }

        return builder.AddField("ℹ️ Help", "`/help` - show this list").Build();
    }
}
