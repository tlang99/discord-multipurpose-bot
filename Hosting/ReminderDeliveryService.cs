using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Services.Reminders;
using ModBot.Services.Storage;

namespace ModBot.Hosting;

/// <summary>
/// Posts reminders when they're due, in the channel they were set in (as a reply to the chat message that asked, if
/// any), pinging only the person. Reminders that came due while the bot was offline are delivered on the first check
/// after it connects, marked as late. If the channel can't be used, it tries a DM instead.
/// </summary>
public class ReminderDeliveryService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>A reminder more than this late gets a note saying so (the bot was offline when it came due).</summary>
    private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    /// <summary>How many checks in a row a reminder may fail to deliver before it's dropped, so a reminder for a deleted
    /// channel with DMs closed doesn't retry forever.</summary>
    private const int MaxAttempts = 5;

    private readonly DiscordSocketClient _client;
    private readonly IReminderStore _store;
    private readonly ILogger<ReminderDeliveryService> _logger;
    private readonly ConcurrentDictionary<string, int> _failedAttempts = new();

    public ReminderDeliveryService(DiscordSocketClient client, IReminderStore store,
        ILogger<ReminderDeliveryService> logger)
    {
        _client = client;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_client.ConnectionState != ConnectionState.Connected)
                {
                    continue;
                }

                try
                {
                    foreach (var reminder in await _store.GetDueAsync(DateTimeOffset.UtcNow))
                    {
                        await DeliverAsync(reminder);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never let one bad check stop reminders for good.
                    _logger.LogError(ex, "Reminder check failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private async Task DeliverAsync(Reminder reminder)
    {
        // Claim it first, so a /reminders cancel at the same moment either wins (and nothing is sent) or loses
        // cleanly, and a slow send can't be picked up twice.
        if (!await _store.RemoveAsync(reminder.Id))
        {
            return;
        }

        var late = DateTimeOffset.UtcNow - reminder.DueAt > LateAfter
            ? $"\n-# This was due {ReminderService.Relative(reminder.DueAt)} - I was offline then."
            : "";
        var mentions = new AllowedMentions(AllowedMentionTypes.None) { UserIds = [reminder.UserId] };

        if (await TrySendInChannelAsync(reminder, late, mentions) || await TrySendDmAsync(reminder, late))
        {
            _failedAttempts.TryRemove(reminder.Id, out _);
            return;
        }

        var attempts = _failedAttempts.AddOrUpdate(reminder.Id, 1, (_, count) => count + 1);
        if (attempts >= MaxAttempts)
        {
            _failedAttempts.TryRemove(reminder.Id, out _);
            _logger.LogWarning("Dropping reminder {Id} for user {UserId} after {Attempts} failed deliveries.",
                reminder.Id, reminder.UserId, attempts);
            return;
        }

        // Put it back for the next check (e.g. Discord had a hiccup).
        await _store.AddAsync(reminder);
    }

    private async Task<bool> TrySendInChannelAsync(Reminder reminder, string late, AllowedMentions mentions)
    {
        if (_client.GetChannel(reminder.ChannelId) is not IMessageChannel channel)
        {
            return false;
        }

        try
        {
            await channel.SendMessageAsync(
                $"⏰ <@{reminder.UserId}>, you asked me to remind you: {reminder.Text}{late}",
                allowedMentions: mentions,
                messageReference: reminder.SourceMessageId is { } source
                    ? new MessageReference(source, reminder.ChannelId, reminder.GuildId, failIfNotExists: false)
                    : null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't post reminder {Id} in channel {ChannelId}; trying a DM.",
                reminder.Id, reminder.ChannelId);
            return false;
        }
    }

    private async Task<bool> TrySendDmAsync(Reminder reminder, string late)
    {
        try
        {
            if (await _client.GetUserAsync(reminder.UserId) is not { } user)
            {
                return false;
            }

            var server = _client.GetGuild(reminder.GuildId)?.Name ?? "a server";
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync($"⏰ Reminder you set in **{Format.Sanitize(server)}** (I couldn't post it there): " +
                                      $"{reminder.Text}{late}", allowedMentions: AllowedMentions.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't DM reminder {Id} to user {UserId}.", reminder.Id, reminder.UserId);
            return false;
        }
    }
}
