using Discord;
using Microsoft.Extensions.Logging;
using ModBot.Models;
using ModBot.Modules.Music;
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
    private readonly ILogger<ServerFeatureSwitch> _logger;

    public ServerFeatureSwitch(ServerSettingsService settings, MusicService music, ILogger<ServerFeatureSwitch> logger)
    {
        _settings = settings;
        _music = music;
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
        var was = await _settings.IsEnabledAsync(guild.Id, feature);
        if (was == on)
        {
            return $"{name} is already {(on ? "on" : "off")} here.";
        }

        await _settings.UpdateAsync(guild.Id, s =>
        {
            s.DisabledFeatures ??= [];
            s.DisabledFeatures.Remove(feature.ToString());
            if (!on)
            {
                s.DisabledFeatures.Add(feature.ToString());
            }
        });
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

        return on
            ? $"✅ {name} is on again."
            : $"🚫 {name} is off on this server.{note} Turn it back on any time.";
    }
}
