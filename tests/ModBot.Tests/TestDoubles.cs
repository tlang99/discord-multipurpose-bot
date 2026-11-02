using System.Reflection;
using ModBot.Models;
using ModBot.Services.Chat;
using ModBot.Services.Storage;

namespace ModBot.Tests;

/// <summary>Guild settings with auto-mod on and "badword" banned.</summary>
internal sealed class FakeGuildSettings : IGuildSettingsStore
{
    public Task<GuildSettings> GetAsync(ulong guildId)
        => Task.FromResult(new GuildSettings { GuildId = guildId, AutoModEnabled = true, BannedWords = ["badword"] });

    public Task UpdateAsync(ulong guildId, Action<GuildSettings> update) => Task.CompletedTask;
}

internal sealed class NoServerPersonality : IChatSettingsStore
{
    public Task<string?> GetPersonalityAsync(ulong guildId) => Task.FromResult<string?>(null);
    public Task SetPersonalityAsync(ulong guildId, string? personality) => Task.CompletedTask;
}

/// <summary>A chat service that answers with fixed text, or throws.</summary>
/// <param name="failJson">Also throw from <see cref="CompleteJsonAsync"/>, like a model that's down.</param>
internal sealed class FakeChat(string reply, bool fail = false, bool failJson = false) : IChatService
{
    public int Calls { get; private set; }

    public Task<string> ReplyAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        Calls++;
        return fail ? throw new ChatServiceException("boom") : Task.FromResult(reply);
    }

    public Task<string> CompleteJsonAsync(string systemPrompt, string userMessage, string jsonSchema,
        CancellationToken cancellationToken = default)
        => failJson ? throw new ChatServiceException("boom") : Task.FromResult(reply);
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

/// <summary>
/// A stand-in for any Discord.Net interface (a member, channel or server), built with <see cref="Create{T}"/>. It
/// answers the members a test sets up, by name (e.g. "get_Id", "GetPermissions"), records every call, and refuses
/// anything else, so a test fails loudly if the code under test starts relying on something new.
/// </summary>
public class DiscordFake : DispatchProxy
{
    public Dictionary<string, object?> Answers { get; private set; } = [];

    /// <summary>The names of the members called, in order.</summary>
    public List<string> Calls { get; } = [];

    public static T Create<T>(Dictionary<string, object?> answers) where T : class
    {
        var fake = DispatchProxy.Create<T, DiscordFake>();
        ((DiscordFake)(object)fake).Answers = answers;
        return fake;
    }

    /// <summary>The fake behind <paramref name="proxy"/>, to look at its calls.</summary>
    public static DiscordFake Of(object proxy) => (DiscordFake)proxy;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? "";
        Calls.Add(name);
        return Answers.TryGetValue(name, out var answer)
            ? answer
            : throw new NotSupportedException($"The fake doesn't support {name}.");
    }
}
