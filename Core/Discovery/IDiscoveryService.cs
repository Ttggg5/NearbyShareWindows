namespace NearbyShare.Core.Discovery;

/// <summary>What this device advertises about itself over mDNS (PROTOCOL.md §1).</summary>
/// <param name="DeviceId">TXT <c>id</c>: this device's stable UUID.</param>
/// <param name="DeviceName">The DNS-SD instance name (sanitized before use).</param>
/// <param name="Fingerprint">TXT <c>fp</c>: lowercase hex SHA-256 of this device's TLS certificate.</param>
/// <param name="Port">TXT <c>port</c>: the TLS listener port.</param>
/// <param name="Os">TXT <c>os</c>: platform identifier.</param>
public readonly record struct LocalDeviceAdvertisement(
    string DeviceId,
    string DeviceName,
    string Fingerprint,
    int Port,
    string Os);

/// <summary>
/// Advertises this device and browses for peers on <c>_nearbyshare._tcp.local.</c>
/// per PROTOCOL.md §1.
/// </summary>
public interface IDiscoveryService : IAsyncDisposable
{
    /// <summary>True once <see cref="StartAsync"/> has completed and the service is live.</summary>
    bool IsRunning { get; }

    /// <summary>A snapshot of the peers currently believed to be present.</summary>
    IReadOnlyList<PeerDevice> Peers { get; }

    /// <summary>Raised for every add/update/remove of a peer. May fire on a background thread.</summary>
    event EventHandler<PeerChange>? PeerChanged;

    /// <summary>Starts advertising this device and browsing for peers.</summary>
    Task StartAsync(LocalDeviceAdvertisement advertisement, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-publishes the local advertisement, e.g. after the user renames the
    /// device or the listener port changes.
    /// </summary>
    Task UpdateAdvertisementAsync(LocalDeviceAdvertisement advertisement, CancellationToken cancellationToken = default);

    /// <summary>Sends a fresh browse query, so peers re-announce without waiting for the timer.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops advertising and browsing, sending a DNS-SD goodbye for the local service.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams peer changes as they happen. Yields one
    /// <see cref="PeerChangeKind.Added"/> entry per already-known peer before
    /// streaming live changes, so a consumer can build its list from a single
    /// subscription.
    /// </summary>
    IAsyncEnumerable<PeerChange> WatchPeersAsync(CancellationToken cancellationToken = default);
}
