using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ModBot.Common;
using ModBot.Models;
using ModBot.Services.Reminders;

namespace ModBot.Hosting;

/// <summary>Posts reminders through the bot's Discord connection, pinging only the person.</summary>
public class DiscordReminderSender : IReminderSender
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DiscordReminderSender> _logger;

    public DiscordReminderSender(DiscordSocketClient client, ILogger<DiscordReminderSender> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ReminderRecipient> CheckRecipientAsync(Reminder reminder)
    {
        if (_client.GetGuild(reminder.GuildId) is not { } guild)
        {
            return ReminderRecipient.NotInServer;
        }

        IGuildUser? member;
        try
        {
            // Falls back to asking Discord if the member isn't cached; null means they're not in the server.
            member = await ((IGuild)guild).GetUserAsync(reminder.UserId, CacheMode.AllowDownload);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't check user {UserId} for reminder {Id}.", reminder.UserId, reminder.Id);
            return ReminderRecipient.Unknown;
        }

        if (member is null)
        {
            return ReminderRecipient.NotInServer;
        }

        return guild.GetChannel(reminder.ChannelId) is { } channel and IMessageChannel &&
               ChannelAccess.CanPost(member, channel) && ChannelAccess.CanPost(guild.CurrentUser, channel)
            ? ReminderRecipient.CanPostInChannel
            : ReminderRecipient.CantPostInChannel;
    }

    public async Task<bool> TrySendInChannelAsync(Reminder reminder, string lateNote)
    {
        if (_client.GetChannel(reminder.ChannelId) is not IMessageChannel channel)
        {
            return false;
        }

        try
        {
            await channel.SendMessageAsync(
                $"⏰ <@{reminder.UserId}>, you asked me to remind you: {reminder.Text}{lateNote}",
                allowedMentions: new AllowedMentions(AllowedMentionTypes.None) { UserIds = [reminder.UserId] },
                // Replies to the chat message that asked, if there was one.
                messageReference: reminder.SourceMessageId is { } source
                    ? new MessageReference(source, reminder.ChannelId, reminder.GuildId, failIfNotExists: false)
                    : null,
                // The text is whatever the person typed; don't let a link in it unfurl into a preview.
                flags: MessageFlags.SuppressEmbeds);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't post reminder {Id} in channel {ChannelId}; trying a DM.",
                reminder.Id, reminder.ChannelId);
            return false;
        }
    }

    public async Task<bool> TrySendDmAsync(Reminder reminder, string lateNote)
    {
        try
        {
            if (await _client.GetUserAsync(reminder.UserId) is not { } user)
            {
                return false;
            }

            var server = _client.GetGuild(reminder.GuildId)?.Name ?? "a server";
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync($"⏰ Reminder you set in **{TextTools.Escape(server)}** (I couldn't post it there): " +
                                      $"{reminder.Text}{lateNote}", allowedMentions: AllowedMentions.None,
                flags: MessageFlags.SuppressEmbeds);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't DM reminder {Id} to user {UserId}.", reminder.Id, reminder.UserId);
            return false;
        }
    }
}
