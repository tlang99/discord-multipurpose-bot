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
/// in between, so changes to part of an object go through <see cref="MutateAsync"/>, which runs under the lock.
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

    public async Task<TValue?> GetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadInternalAsync();
            return data.TryGetValue(key, out var value) ? Clone(value) : default;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyCollection<TValue>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadInternalAsync();
            return data.Values.Select(Clone).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Runs <paramref name="mutation"/> under the lock, then saves. It works on a copy that only replaces the
    /// cached data once the file is written, so if the mutation throws partway or the save fails (e.g. disk full),
    /// nothing changes - memory never holds data the file doesn't. Don't keep references to the values it touches
    /// after it returns.</summary>
    public async Task MutateAsync(Action<Dictionary<string, TValue>> mutation)
    {
        await _lock.WaitAsync();
        try
        {
            var working = Clone(await LoadInternalAsync());
            mutation(working);
            await SaveInternalAsync(working);
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
