namespace NearbyShare.Core.Persistence;

/// <summary>
/// A general key/value settings store for the app. Deliberately not shaped
/// around the single MVP setting (device name) so future settings — default save
/// folder, auto-accept from known peers, clipboard sharing toggles — slot in
/// without touching this interface or its consumers.
/// </summary>
public interface IDeviceSettingsRepository
{
    /// <summary>Reads a raw string setting, or null if it has never been set.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes a setting. A null value removes the key.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a setting, storing and returning <paramref name="factory"/>'s result
    /// if the key is absent. Used for values that must be generated once and then
    /// stay stable, such as the device UUID.
    /// </summary>
    Task<string> GetOrCreateAsync(string key, Func<string> factory, CancellationToken cancellationToken = default);

    /// <summary>All settings currently stored.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a setting. Returns true if it existed.</summary>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Well-known setting keys.</summary>
public static class SettingKeys
{
    /// <summary>The user-visible device name, advertised as the DNS-SD instance name.</summary>
    public const string DeviceName = "device.name";

    /// <summary>This device's stable UUID, i.e. the mDNS TXT <c>id</c> field.</summary>
    public const string DeviceId = "device.id";

    /// <summary>Folder that received files are written to.</summary>
    public const string DownloadFolder = "transfer.downloadFolder";

    /// <summary>Preferred TCP listener port; 0 means let the OS choose.</summary>
    public const string ListenPort = "transfer.listenPort";
}
