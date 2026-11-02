using ModBot.Models;

namespace ModBot.Services.Storage;

public class JsonMusicQueueStore : IMusicQueueStore
{
    private readonly JsonFileRepository<MusicQueueState> _repository;

    public JsonMusicQueueStore() : this(Path.Combine("data", "music-queue.json"))
    {
    }

    /// <summary>For tests: a store backed by a file of your choosing.</summary>
    public JsonMusicQueueStore(string filePath)
    {
        _repository = new JsonFileRepository<MusicQueueState>(filePath);
    }

    public async Task<MusicQueueState> GetAsync(ulong guildId)
        => await _repository.GetAsync(guildId.ToString()) ?? new MusicQueueState { GuildId = guildId };

    public Task<IReadOnlyCollection<MusicQueueState>> GetAllAsync() => _repository.GetAllAsync();

    public Task<T> MutateAsync<T>(ulong guildId, Func<MusicQueueState, T> change)
        => _repository.MutateOrCreateAsync(guildId.ToString(), () => new MusicQueueState { GuildId = guildId }, change);
}
