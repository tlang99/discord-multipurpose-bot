namespace ModBot.Models;

/// <summary>Per-guild chatbot settings, kept apart from <see cref="GuildSettings"/> (moderation) on purpose.</summary>
public class ChatGuildSettings
{
    public ulong GuildId { get; set; }

    /// <summary>The server's own personality for the chatbot, set with /chat personality. Null uses the default
    /// (<c>Chat:Personality</c> in appsettings.json).</summary>
    public string? Personality { get; set; }
}
