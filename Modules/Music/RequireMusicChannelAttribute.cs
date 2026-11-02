using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ModBot.Services.Settings;

namespace ModBot.Modules.Music;

/// <summary>
/// Limits a command to the server's music channel (its own choice from /settings channel, or the bot's default
/// <c>Music:ChannelName</c>), so music replies and "Now playing" announcements stay out of the main chat. Anywhere
/// else, the caller gets a private reply pointing them to the right channel.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireMusicChannelAttribute : PreconditionAttribute
{
    public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context,
        ICommandInfo commandInfo, IServiceProvider services)
    {
        if (context.Guild is null)
        {
            return PreconditionResult.FromSuccess();
        }

        var music = await services.GetRequiredService<ServerSettingsService>().MusicChannelAsync(context.Guild);
        return music.Allows(context.Channel)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(music.GoThere("Music commands go", "them"));
    }
}
