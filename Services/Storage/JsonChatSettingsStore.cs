using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonChatSettingsStore : IChatSettingsStore
{
    private readonly JsonFileRepository<ChatGuildSettings> _repository =
        JsonFileRepository<ChatGuildSettings>.InDataFolder("chat-settings.json");

    public async Task<string?> GetPersonalityAsync(ulong guildId)
        => (await _repository.GetAsync(guildId.ToString()))?.Personality;

    public Task SetPersonalityAsync(ulong guildId, string? personality)
        => personality is null
            ? _repository.RemoveAsync(guildId.ToString())
            : _repository.MutateAsync(data =>
                data[guildId.ToString()] = new ChatGuildSettings { GuildId = guildId, Personality = personality });
}
