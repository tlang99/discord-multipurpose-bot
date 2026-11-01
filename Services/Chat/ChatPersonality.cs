using Microsoft.Extensions.Options;
using ModBot.Configuration;
using ModBot.Services.Storage;

namespace ModBot.Services.Chat;

/// <summary>The personality the bot uses in a server: the server's own (/chat personality), else the default from
/// <c>Chat:Personality</c>, else the built-in one. Shared by the chatbot and news write-ups.</summary>
public class ChatPersonality
{
    private readonly IChatSettingsStore _store;
    private readonly ChatOptions _options;

    public ChatPersonality(IChatSettingsStore store, IOptions<ChatOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task<string> GetAsync(ulong guildId)
    {
        var personality = await _store.GetPersonalityAsync(guildId);
        if (!string.IsNullOrWhiteSpace(personality))
        {
            return personality.Trim();
        }

        return string.IsNullOrWhiteSpace(_options.Personality) ? ChatOptions.DefaultPersonality : _options.Personality.Trim();
    }

    /// <summary>The start of every system prompt: who the bot is and its personality. Callers add their own task
    /// instructions after it.</summary>
    public async Task<string> IntroAsync(ulong guildId, string botName, string serverName)
        => $"You are {botName}, the bot in the Discord server \"{serverName}\".\n\n" +
           $"Your personality: {await GetAsync(guildId)}\n\n";
}
