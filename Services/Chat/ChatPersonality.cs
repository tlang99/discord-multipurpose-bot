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
}
