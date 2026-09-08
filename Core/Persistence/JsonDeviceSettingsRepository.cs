using System.Text.Json;

namespace NearbyShare.Core.Persistence;

/// <summary>
/// JSON-file-backed <see cref="IDeviceSettingsRepository"/>. The whole document is
/// small enough to hold in memory and rewrite atomically on every change.
/// </summary>
public sealed class JsonDeviceSettingsRepository : IDeviceSettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private Dictionary<string, string>? _cache;

    public JsonDeviceSettingsRepository(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return settings.TryGetValue(key, out string? value) ? value : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> settings = await LoadAsync(cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                if (!settings.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (settings.TryGetValue(key, out string? existing) && existing == value)
                {
                    return;
                }

                settings[key] = value;
            }

            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> GetOrCreateAsync(string key, Func<string> factory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings.TryGetValue(key, out string? existing) && !string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            string created = factory();
            settings[key] = created;
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            return created;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, string>(settings);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> settings = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Remove(key))
            {
                return false;
            }

            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return _cache;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions)
                     ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt settings file falls back to defaults rather than blocking
            // startup; the next write repairs it.
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return _cache;
    }

    private async Task SaveAsync(Dictionary<string, string> settings, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, SerializerOptions);

        // Write-then-rename, so an interrupted write cannot leave a half-written
        // settings file that would lose the persisted device id.
        string temporaryPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
