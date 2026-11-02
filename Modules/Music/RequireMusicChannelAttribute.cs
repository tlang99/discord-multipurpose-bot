using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModBot.Common;
using ModBot.Configuration;

namespace ModBot.Modules.Music;

/// <summary>
/// Limits a command to the channel named by <see cref="MusicOptions.ChannelName"/>, so music replies and "Now playing"
/// announcements (which go to the channel /play was used in) stay out of the main chat. Anywhere else, the caller gets
/// a private reply pointing them to the right channel.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireMusicChannelAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo,
        IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<MusicOptions>>().Value;
        var channelName = ConfiguredChannels.ChannelName(options.ChannelName);
        if (channelName.Length == 0 || context.Guild is null ||
            context.Channel is ITextChannel channel && ConfiguredChannels.IsNamed(channel, channelName))
        {
            return Task.FromResult(PreconditionResult.FromSuccess());
        }

        return CheckAsync(context.Guild, channelName);
    }

    private static async Task<PreconditionResult> CheckAsync(IGuild guild, string channelName)
    {
        var musicChannel = (await guild.GetTextChannelsAsync(CacheMode.CacheOnly))
            .FirstOrDefault(c => ConfiguredChannels.IsNamed(c, channelName));

        return PreconditionResult.FromError(musicChannel is null
            ? ConfiguredChannels.Missing("Music commands go", channelName)
            : $"Music commands go in {musicChannel.Mention} - run them there.");
    }
}
