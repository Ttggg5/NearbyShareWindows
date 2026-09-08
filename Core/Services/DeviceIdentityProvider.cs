using NearbyShare.Core.Networking;
using NearbyShare.Core.Persistence;

namespace NearbyShare.Core.Services;

/// <summary>
/// Supplies this device's stable id and user-chosen name, both persisted through
/// <see cref="IDeviceSettingsRepository"/>.
/// </summary>
public interface IDeviceIdentityProvider
{
    /// <summary>Returns the device id and name, generating the id on first run.</summary>
    Task<LocalIdentity> GetIdentityAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames the device. Callers must re-advertise afterwards.</summary>
    Task SetDeviceNameAsync(string deviceName, CancellationToken cancellationToken = default);
}

/// <summary>Settings-backed <see cref="IDeviceIdentityProvider"/>.</summary>
public sealed class DeviceIdentityProvider : IDeviceIdentityProvider
{
    private readonly IDeviceSettingsRepository _settings;
    private readonly Func<string> _defaultDeviceNameFactory;

    /// <param name="settings">Where the id and name are persisted.</param>
    /// <param name="defaultDeviceNameFactory">
    /// Produces the initial device name. Defaults to the machine name, which is a
    /// better first impression than a UUID.
    /// </param>
    public DeviceIdentityProvider(IDeviceSettingsRepository settings, Func<string>? defaultDeviceNameFactory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _defaultDeviceNameFactory = defaultDeviceNameFactory ?? (() => Environment.MachineName);
    }

    /// <inheritdoc />
    public async Task<LocalIdentity> GetIdentityAsync(CancellationToken cancellationToken = default)
    {
        // PROTOCOL.md §1: the id is a v4 UUID generated once and persisted.
        string deviceId = await _settings
            .GetOrCreateAsync(SettingKeys.DeviceId, () => Guid.NewGuid().ToString(), cancellationToken)
            .ConfigureAwait(false);

        string deviceName = await _settings
            .GetOrCreateAsync(SettingKeys.DeviceName, _defaultDeviceNameFactory, cancellationToken)
            .ConfigureAwait(false);

        return new LocalIdentity(deviceId, deviceName);
    }

    /// <inheritdoc />
    public Task SetDeviceNameAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        return _settings.SetAsync(SettingKeys.DeviceName, deviceName.Trim(), cancellationToken);
    }
}
