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

    /// <summary>All the text in an embed (title, description, fields, footer, and its links, which can come from a web
    /// page), for the word filter.</summary>
    public static string EmbedText(Embed? embed)
        => embed is null
            ? ""
            : string.Join("\n", new[] { embed.Title, embed.Description, embed.Footer?.Text, embed.Url, embed.Image?.Url }
                .Concat(embed.Fields.SelectMany(f => new[] { f.Name, f.Value }))
                .Where(s => !string.IsNullOrEmpty(s)));

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
