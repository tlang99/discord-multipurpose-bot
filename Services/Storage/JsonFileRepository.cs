using System.Text.Json;

namespace ModBot.Services.Storage;

/// <summary>
/// Minimal thread-safe JSON-file-backed key/value store shared by the settings
/// and warnings stores. Swap this out for a real database later by implementing
/// the same read/mutate pattern against your DB of choice instead of a file.
/// </summary>
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
        var data = await LoadAsync();
        return data.TryGetValue(key, out var value) ? value : default;
    }

    public async Task<IReadOnlyCollection<TValue>> GetAllAsync()
    {
        var data = await LoadAsync();
        return data.Values.ToList();
    }

    public async Task SetAsync(string key, TValue value)
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadInternalAsync();
            data[key] = value;
            await SaveInternalAsync(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MutateAsync(Action<Dictionary<string, TValue>> mutation)
    {
        await _lock.WaitAsync();
        try
        {
            var data = await LoadInternalAsync();
            mutation(data);
            await SaveInternalAsync(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, TValue>> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

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
        _cache = data;
        using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, data, SerializerOptions);
    }
}
