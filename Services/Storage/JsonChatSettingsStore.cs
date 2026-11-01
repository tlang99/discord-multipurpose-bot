using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonChatSettingsStore : IChatSettingsStore
{
    private readonly JsonFileRepository<ChatGuildSettings> _repository;

    public JsonChatSettingsStore()
    {
        _repository = new JsonFileRepository<ChatGuildSettings>(Path.Combine("data", "chat-settings.json"));
    }

    public async Task<string?> GetPersonalityAsync(ulong guildId)
        => (await _repository.GetAsync(guildId.ToString()))?.Personality;

    public Task SetPersonalityAsync(ulong guildId, string? personality)
        => _repository.MutateAsync(data =>
        {
            var key = guildId.ToString();
            if (personality is null)
            {
                data.Remove(key);
                return;
            }

            data[key] = new ChatGuildSettings { GuildId = guildId, Personality = personality };
        });
}
