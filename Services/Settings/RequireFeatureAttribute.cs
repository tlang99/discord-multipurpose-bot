using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ModBot.Models;

namespace ModBot.Services.Settings;

/// <summary>Refuses a command, button or pop-up when the server has turned <paramref name="feature"/> off with
/// /settings feature, with a private reply saying so.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute(ServerFeature feature) : PreconditionAttribute
{
    public ServerFeature Feature { get; } = feature;

    public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context,
        ICommandInfo commandInfo, IServiceProvider services)
    {
        if (context.Guild is null ||
            await services.GetRequiredService<ServerSettingsService>().IsEnabledAsync(context.Guild.Id, Feature))
        {
            return PreconditionResult.FromSuccess();
        }

        return PreconditionResult.FromError(ServerSettingsService.TurnedOff(Feature));
    }
}
