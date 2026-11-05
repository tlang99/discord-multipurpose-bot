using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ModBot.Models;
using ModBot.Services.Settings;

namespace ModBot.Modules.Voice;

/// <summary>/voice listen and /voice stop: start and stop listening for voice commands in your voice channel. The
/// same as asking the chatbot ("@bot listen in voice", "hey ModBot, stop listening"); the work is
/// <see cref="VoiceListener"/>'s.</summary>
[Group("voice", "Voice commands: say \"hey ModBot\" in a voice channel to ask it things.")]
[RequireContext(ContextType.Guild)]
public class VoiceModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly VoiceListener _voice;

    public VoiceModule(VoiceListener voice)
    {
        _voice = voice;
    }

    // Only starting needs the feature on. Stopping always works, so nobody's ever left unable to stop it, whatever
    // the switch says.
    [RequireFeature(ServerFeature.VoiceCommands)]
    [SlashCommand("listen", "Joins your voice channel and listens for \"hey ModBot...\" requests.")]
    public async Task ListenAsync()
    {
        if (Context.User is not SocketGuildUser user || Context.Channel is not SocketTextChannel channel)
        {
            await RespondAsync("Use this in a server text channel (or a voice channel's chat).", ephemeral: true);
            return;
        }

        // Joining can take a few seconds, longer than Discord waits for a first reply.
        await DeferAsync(ephemeral: true);
        var answer = await _voice.StartAsync(user, channel);
        await FollowupAsync(answer.Length > 0 ? answer : "🎙️ Listening.", ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("stop", "Stops listening for voice commands and leaves the voice channel.")]
    public async Task StopAsync()
    {
        if (Context.User is not SocketGuildUser user)
        {
            return;
        }

        await DeferAsync(ephemeral: true);
        var answer = await _voice.StopForAsync(user);
        await FollowupAsync(answer, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
