using Discord;
using Discord.Net;
using Microsoft.Extensions.Logging;

namespace ModBot.Common;

/// <summary>Small Discord message chores shared by several features.</summary>
public static class MessageTools
{
    /// <summary>A link that jumps to a message.</summary>
    public static string Link(ulong guildId, ulong channelId, ulong messageId)
        => $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    /// <summary>Pins <paramref name="message"/>; false (and logged) if Discord refuses, e.g. without Pin
    /// Messages.</summary>
    /// <param name="what">For the log, e.g. "the music menu".</param>
    public static async Task<bool> TryPinAsync(IUserMessage message, ILogger logger, string what)
    {
        try
        {
            await message.PinAsync();
            return true;
        }
        catch (HttpException ex)
        {
            logger.LogInformation(ex, "Couldn't pin {What} in channel {ChannelId}.", what, message.Channel.Id);
            return false;
        }
    }
}
