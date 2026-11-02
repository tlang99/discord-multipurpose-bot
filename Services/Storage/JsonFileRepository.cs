using System.Text.Json;

namespace ModBot.Services.Storage;

/// <summary>
/// Minimal thread-safe JSON-file-backed key/value store shared by the settings
/// and warnings stores. Swap this out for a real database later by implementing
/// the same read/mutate pattern against your DB of choice instead of a file.
/// </summary>
/// <remarks>
/// Reads hand out copies, never the cached objects themselves: otherwise a caller enumerating a list on one (e.g.
/// auto-mod reading the banned words) could be interrupted by a command changing that same list, which throws
/// "Collection was modified". The flip side is that a read-modify-save of a whole object can overwrite a change made
/// in between, so changes to part of an object go through <see cref="MutateAsync{T}"/>, which runs under the lock.
/// </remarks>
public class JsonFileRepository<TValue>
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, TValue>? _cache;

    public JsonFileRepository(string filePath)
    {
        _filePath = filePath;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>A repository for <c>data/<paramref name="fileName"/></c>, where the bot keeps its files.</summary>
    public static JsonFileRepository<TValue> InDataFolder(string fileName) => new(Path.Combine("data", fileName));

    public Task<TValue?> GetAsync(string key)
        => ReadAsync(data => data.TryGetValue(key, out var value) ? Clone(value) : default);

    public Task<IReadOnlyCollection<TValue>> GetAllAsync()
        => ReadAsync<IReadOnlyCollection<TValue>>(data => data.Values.Select(Clone).ToList());

    /// <summary>Runs <paramref name="mutation"/> under the lock, saves, and returns what it returned. It works on a
    /// copy that only replaces the cached data once the file is written, so if the mutation throws partway or the save
    /// fails (e.g. disk full), nothing changes - memory never holds data the file doesn't. Don't keep references to the
    /// values it touches after it returns.</summary>
    public async Task<T> MutateAsync<T>(Func<Dictionary<string, TValue>, T> mutation)
    {
        await _lock.WaitAsync();
        try
        {
            var working = Clone(await LoadInternalAsync());
            var result = mutation(working);
            await SaveInternalAsync(working);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task MutateAsync(Action<Dictionary<string, TValue>> mutation)
        => MutateAsync(data =>
        {
            mutation(data);
            return true;
        });

    /// <summary>Changes the value at <paramref name="key"/>, creating it with <paramref name="create"/> first if
    /// there isn't one, and returns what <paramref name="change"/> returned.</summary>
    public Task<T> MutateOrCreateAsync<T>(string key, Func<TValue> create, Func<TValue, T> change)
        => MutateAsync(data =>
        {
            if (!data.TryGetValue(key, out var value))
            {
                data[key] = value = create();
            }

            return change(value);
        });

    /// <summary>Changes the value at <paramref name="key"/> if there is one; false (and nothing saved changes) if
    /// there isn't.</summary>
    public Task<bool> UpdateAsync(string key, Action<TValue> change)
        => MutateAsync(data =>
        {
            if (!data.TryGetValue(key, out var value))
            {
                return false;
            }

            change(value);
            return true;
        });

    /// <summary>Removes the value at <paramref name="key"/>; false if it was already gone.</summary>
    public Task<bool> RemoveAsync(string key) => MutateAsync(data => data.Remove(key));

    private async Task<T> ReadAsync<T>(Func<Dictionary<string, TValue>, T> read)
    {
        await _lock.WaitAsync();
        try
        {
            return read(await LoadInternalAsync());
        }
        finally
        {
            _lock.Release();
        }
    }

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions),
            SerializerOptions)!;

    private async Task<Dictionary<string, TValue>> LoadInternalAsync()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, TValue>();
            return _cache;
        }

        using var stream = File.OpenRead(_filePath);
        var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, TValue>>(stream, SerializerOptions);
        _cache = loaded ?? new Dictionary<string, TValue>();
        return _cache;
    }

    private async Task SaveInternalAsync(Dictionary<string, TValue> data)
    {
        // Write a temp file and swap it in, so a crash or full disk mid-write can't leave a truncated file that
        // fails to load on every later start.
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, data, SerializerOptions);
        }

        File.Move(tempPath, _filePath, overwrite: true);
        _cache = data;
    }
}
