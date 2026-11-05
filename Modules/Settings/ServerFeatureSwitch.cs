using Discord;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Modules.Music;
using ModBot.Modules.Voice;
using ModBot.Services.Settings;

namespace ModBot.Modules.Settings;

/// <summary>
/// Turns a feature on or off for one server, shared by /settings feature and the chatbot ("@bot disable polls") so
/// both do the same thing: save it, and for music, stop what's playing (otherwise it would keep playing with every
/// music control turned off). Things already in progress elsewhere are left alone: open LFG posts can still be
/// cancelled, open polls ended, and pending reminders are still delivered.
/// </summary>
public class ServerFeatureSwitch
{
    private readonly ServerSettingsService _settings;
    private readonly MusicService _music;
    private readonly VoiceListener _voice;
    private readonly ILogger<ServerFeatureSwitch> _logger;

    public ServerFeatureSwitch(ServerSettingsService settings, MusicService music, VoiceListener voice,
        ILogger<ServerFeatureSwitch> logger)
    {
        _settings = settings;
        _music = music;
        _voice = voice;
        _logger = logger;
    }

    /// <summary>Who may change server settings: Manage Server, like every other /settings, /automod and /chat
    /// command.</summary>
    public static bool CanChange(IGuildUser user) => user.GuildPermissions.ManageGuild;

    /// <summary>Saves the change and returns what to tell the admin.</summary>
    /// <param name="by">Who changed it, for the log.</param>
    public async Task<string> SetAsync(IGuild guild, ServerFeature feature, bool on, IUser by)
    {
        var name = ServerSettingsService.NameOf(feature);

        // Checked and changed in one step, under the store's lock, so two admins switching it at once both get a true
        // answer (one "turned off", one "already off").
        var changed = false;
        await _settings.UpdateAsync(guild.Id, s =>
        {
            if (s.IsEnabled(feature) == on)
            {
                return;
            }

            s.Set(feature, on);
            changed = true;
        });

        if (!changed)
        {
            return $"{name} is already {(on ? "on" : "off")} here.";
        }

        _logger.LogInformation("{Feature} turned {State} in server {Server} ({GuildId}) by {User} ({UserId}).",
            feature, on ? "on" : "off", guild.Name, guild.Id, by.Username, by.Id);

        var note = "";
        if (!on && feature == ServerFeature.Music && _music.GetPlayer(guild.Id) is { } player)
        {
            try
            {
                await player.StopAsync();
                await player.DisposeAsync(); // leaves voice; the queue is kept for if music comes back on
                note = " I stopped the music and left voice; the queue's kept.";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't stop the music after it was turned off in guild {GuildId}.", guild.Id);
            }
        }

        if (!on && feature == ServerFeature.VoiceCommands && _voice.IsListening(guild.Id))
        {
            await _voice.StopAsync(guild.Id, null);
            note = " I stopped listening and left voice.";
        }

        if (on && feature == ServerFeature.VoiceCommands)
        {
            // On, but the bot itself may not be able to: tell the admin now rather than at the first "listen".
            note = _voice.SetupProblem is { } problem
                ? $" But voice commands aren't set up on this bot yet - for whoever runs it: {problem}"
                : " Join a voice channel and use `/voice listen` (or ask me to \"listen in voice\") to start.";
        }

        return on
            ? ServerSettings.IsOptIn(feature) ? $"✅ {name} is on.{note}" : $"✅ {name} is on again."
            : $"🚫 {name} is off on this server.{note} Turn it back on any time.";
    }
}
