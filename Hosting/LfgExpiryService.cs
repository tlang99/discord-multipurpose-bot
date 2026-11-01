using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModBot.Modules.Lfg;
using ModBot.Services.Lfg;

namespace ModBot.Hosting;

/// <summary>Closes LFG posts that didn't fill up in time, marking their message as expired.</summary>
public class LfgExpiryService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly ILfgService _lfg;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<LfgExpiryService> _logger;

    public LfgExpiryService(ILfgService lfg, DiscordSocketClient client, ILogger<LfgExpiryService> logger)
    {
        _lfg = lfg;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var post in _lfg.TakeExpired(DateTimeOffset.UtcNow))
                {
                    if (post.MessageId == 0 || _client.GetChannel(post.ChannelId) is not IMessageChannel channel)
                    {
                        continue;
                    }

                    try
                    {
                        await channel.ModifyMessageAsync(post.MessageId, m =>
                        {
                            m.Embed = LfgMessages.ExpiredEmbed(post);
                            m.Components = new ComponentBuilder().Build();
                        });
                    }
                    catch (Exception ex)
                    {
                        // Deleted message, lost channel access, or disconnected. The post is closed regardless; its
                        // buttons will say so if anyone clicks them.
                        _logger.LogInformation(ex, "Couldn't mark expired LFG {MessageId} in channel {ChannelId}.",
                            post.MessageId, post.ChannelId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }
}
