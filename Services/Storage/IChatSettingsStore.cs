namespace ModBot.Services.Storage;

public interface IChatSettingsStore
{
    /// <summary>The server's own chatbot personality, or null if it uses the default.</summary>
    Task<string?> GetPersonalityAsync(ulong guildId);

    /// <summary>Sets the server's personality; null goes back to the default.</summary>
    Task SetPersonalityAsync(ulong guildId, string? personality);
}
