using NearbyShare.Core.Networking;

namespace NearbyShare.Core.Tests.Networking;

/// <summary>An in-memory <see cref="IKnownPeerStore"/> with the same overwrite rules as the real one.</summary>
internal sealed class InMemoryKnownPeerStore : IKnownPeerStore
{
    private readonly Dictionary<string, KnownPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times <see cref="Pin"/> actually created a new pin.</summary>
    public int PinCount { get; private set; }

    public string? GetPinnedFingerprint(string deviceId) =>
        _peers.TryGetValue(deviceId, out KnownPeer? peer) ? peer.Fingerprint : null;

    public void Pin(string deviceId, string fingerprint, string? deviceName = null)
    {
        string normalized = CertificateManager.NormalizeFingerprint(fingerprint);

        if (_peers.TryGetValue(deviceId, out KnownPeer? existing))
        {
            if (!string.Equals(existing.Fingerprint, normalized, StringComparison.Ordinal))
            {
                throw new PeerIdentityChangedException(deviceId, existing.Fingerprint, normalized);
            }

            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _peers[deviceId] = new KnownPeer(deviceId, normalized, deviceName, now, now);
        PinCount++;
    }

    public void ReplacePin(string deviceId, string fingerprint, string? deviceName = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _peers[deviceId] = new KnownPeer(
            deviceId,
            CertificateManager.NormalizeFingerprint(fingerprint),
            deviceName,
            now,
            now);
    }

    public bool Forget(string deviceId) => _peers.Remove(deviceId);

    public IReadOnlyList<KnownPeer> GetAll() => _peers.Values.ToList();
}
