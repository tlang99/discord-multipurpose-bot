using ModBot.Models;
using ModBot.Services.Chat;
using ModBot.Services.Storage;

namespace ModBot.Tests;

/// <summary>Guild settings with auto-mod on and "badword" banned.</summary>
internal sealed class FakeGuildSettings : IGuildSettingsStore
{
    public Task<GuildSettings> GetAsync(ulong guildId)
        => Task.FromResult(new GuildSettings { GuildId = guildId, BannedWords = ["badword"] });

    public Task UpdateAsync(ulong guildId, Action<GuildSettings> update) => Task.CompletedTask;
}

internal sealed class NoServerPersonality : IChatSettingsStore
{
    public Task<string?> GetPersonalityAsync(ulong guildId) => Task.FromResult<string?>(null);
    public Task SetPersonalityAsync(ulong guildId, string? personality) => Task.CompletedTask;
}

/// <summary>A chat service that answers with fixed text, or throws.</summary>
internal sealed class FakeChat(string reply, bool fail = false) : IChatService
{
    public int Calls { get; private set; }

    public Task<string> ReplyAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        Calls++;
        return fail ? throw new ChatServiceException("boom") : Task.FromResult(reply);
    }

    public Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, string jsonSchema,
        CancellationToken cancellationToken = default) => Task.FromResult(reply);
}

/// <summary>A JSON reminder store in a temp folder, deleted afterwards.</summary>
internal sealed class TempReminderStore : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "modbot-tests", Guid.NewGuid().ToString("N"));

    public TempReminderStore() => Store = new JsonReminderStore(FilePath);

    public JsonReminderStore Store { get; }

    public string FilePath => Path.Combine(_directory, "reminders.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
