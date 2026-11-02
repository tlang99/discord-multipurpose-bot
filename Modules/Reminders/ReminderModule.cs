using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ModBot.Common;
using ModBot.Services.Moderation;
using ModBot.Services.Reminders;

namespace ModBot.Modules.Reminders;

/// <summary>/remind sets a reminder; the bot pings the person in the same channel when it's due. The rules live in
/// <see cref="ReminderService"/>, shared with the chatbot.</summary>
[RequireContext(ContextType.Guild)]
public class ReminderModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ReminderService _reminders;

    public ReminderModule(ReminderService reminders)
    {
        _reminders = reminders;
    }

    [SlashCommand("remind", "Sets a reminder - I'll ping you in this channel when it's time.")]
    public async Task RemindAsync(
        [Summary("when", "e.g. 10m, 2h30m, in 3 days, at 5pm, tomorrow 9am")] [MaxLength(100)] string when,
        [Summary("what", "What to remind you about")] [MaxLength(ReminderService.MaxTextLength)] string what)
    {
        if (Context.Channel is not ITextChannel channel)
        {
            await RespondAsync("Reminders only work in a server text channel.", ephemeral: true);
            return;
        }

        // Slash commands work even where the person can't type, so without this anyone could have the bot post their
        // text in a read-only channel like #rules.
        if (Context.User is not SocketGuildUser member || !ChannelAccess.CanPost(member, channel))
        {
            await RespondAsync($"You can't post in {channel.Mention}, so I can't post a reminder there for you.",
                ephemeral: true);
            return;
        }

        // It's delivered here later, so make sure that'll work now rather than failing silently then.
        if (!ChannelAccess.CanPost(Context.Guild.CurrentUser, channel))
        {
            await RespondAsync($"I can't post in {channel.Mention}, so I couldn't deliver a reminder here. Try another " +
                               "channel.", ephemeral: true);
            return;
        }

        // Auto-mod exempts members with Manage Messages from the word filter; same here.
        var applyWordFilter = !TextFilter.IsExempt(member);
        var result = await _reminders.CreateAsync(Context.Guild.Id, channel.Id, Context.User.Id, when, what,
            applyWordFilter);
        await RespondAsync(result.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [Group("reminders", "See or cancel your reminders.")]
    public class RemindersModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly ReminderService _reminders;

        public RemindersModule(ReminderService reminders)
        {
            _reminders = reminders;
        }

        [SlashCommand("list", "Shows your pending reminders in this server.")]
        public async Task ListAsync()
        {
            var reminders = await _reminders.ListAsync(Context.Guild.Id, Context.User.Id);
            if (reminders.Count == 0)
            {
                await RespondAsync("You don't have any reminders here. Set one with `/remind`.", ephemeral: true);
                return;
            }

            var text = new StringBuilder();
            foreach (var reminder in reminders)
            {
                var line = $"`{reminder.Id}` {ReminderService.Relative(reminder.DueAt)} in <#{reminder.ChannelId}>: " +
                           $"{TextTools.Truncate(reminder.Text, 120)}";
                if (text.Length + line.Length > EmbedBuilder.MaxDescriptionLength - 50)
                {
                    text.AppendLine("…");
                    break;
                }

                text.AppendLine(line);
            }

            var embed = new EmbedBuilder()
                .WithColor(Colors.Bot)
                .WithTitle($"Your reminders ({reminders.Count})")
                .WithDescription(text.ToString())
                .WithFooter("Cancel one with /reminders cancel and its code.")
                .Build();
            await RespondAsync(embed: embed, ephemeral: true);
        }

        [SlashCommand("cancel", "Cancels one of your reminders.")]
        public async Task CancelAsync([Summary("id", "Its code from /reminders list, e.g. k3x9")] string id)
        {
            var result = await _reminders.CancelAsync(Context.Guild.Id, Context.User.Id, id);
            await RespondAsync(result.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
        }
    }
}
