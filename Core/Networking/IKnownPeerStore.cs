namespace NearbyShare.Core.Networking;

/// <summary>
/// Persists the trust-on-first-use pins from PROTOCOL.md §2 step 4: one
/// certificate fingerprint per peer device id.
/// </summary>
public interface IKnownPeerStore
{
    /// <summary>Returns the pinned fingerprint for <paramref name="deviceId"/>, or null if this peer is new.</summary>
    string? GetPinnedFingerprint(string deviceId);

    /// <summary>
    /// Pins <paramref name="fingerprint"/> for a peer seen for the first time.
    /// Implementations must NOT silently overwrite an existing, different pin —
    /// that is the security event PROTOCOL.md §2 step 4 requires be surfaced.
    /// </summary>
    void Pin(string deviceId, string fingerprint, string? deviceName = null);

    /// <summary>
    /// Replaces an existing pin. Only ever called after the user has explicitly
    /// confirmed a peer's identity change.
    /// </summary>
    void ReplacePin(string deviceId, string fingerprint, string? deviceName = null);

    /// <summary>Forgets a peer, so the next connection is treated as first use again.</summary>
    bool Forget(string deviceId);

    /// <summary>All currently pinned peers.</summary>
    IReadOnlyList<KnownPeer> GetAll();
}

/// <summary>A pinned peer identity.</summary>
/// <param name="DeviceId">The peer's stable device UUID (the mDNS TXT <c>id</c>).</param>
/// <param name="Fingerprint">The pinned lowercase hex SHA-256 certificate fingerprint.</param>
/// <param name="DeviceName">The last-seen display name, for showing in warnings. Not part of trust.</param>
/// <param name="FirstSeen">When the pin was created.</param>
/// <param name="LastSeen">When the pin was last confirmed by a successful handshake.</param>
public sealed record KnownPeer(
    string DeviceId,
    string Fingerprint,
    string? DeviceName,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
