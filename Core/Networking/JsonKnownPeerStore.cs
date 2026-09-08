using System.Text.Json;
using System.Text.Json.Serialization;

namespace NearbyShare.Core.Networking;

/// <summary>
/// File-backed <see cref="IKnownPeerStore"/>. Pins live in a small JSON document
/// in local app data; the whole map is held in memory and rewritten atomically on
/// change, which is appropriate for the handful of peers on a home network.
/// </summary>
public sealed class JsonKnownPeerStore : IKnownPeerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, KnownPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public JsonKnownPeerStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <inheritdoc />
    public string? GetPinnedFingerprint(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureLoaded();
            return _peers.TryGetValue(deviceId, out KnownPeer? peer) ? peer.Fingerprint : null;
        }
    }

    /// <inheritdoc />
    public void Pin(string deviceId, string fingerprint, string? deviceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        string normalized = CertificateManager.NormalizeFingerprint(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized, nameof(fingerprint));

        lock (_gate)
        {
            EnsureLoaded();

            if (_peers.TryGetValue(deviceId, out KnownPeer? existing))
            {
                if (!string.Equals(existing.Fingerprint, normalized, StringComparison.Ordinal))
                {
                    // PROTOCOL.md §2 step 4: never silently overwrite a pin.
                    throw new PeerIdentityChangedException(deviceId, existing.Fingerprint, normalized);
                }

                _peers[deviceId] = existing with
                {
                    DeviceName = deviceName ?? existing.DeviceName,
                    LastSeen = DateTimeOffset.UtcNow,
                };
            }
            else
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                _peers[deviceId] = new KnownPeer(deviceId, normalized, deviceName, now, now);
            }

            Save();
        }
    }

    /// <inheritdoc />
    public void ReplacePin(string deviceId, string fingerprint, string? deviceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        string normalized = CertificateManager.NormalizeFingerprint(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized, nameof(fingerprint));

        lock (_gate)
        {
            EnsureLoaded();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset firstSeen = _peers.TryGetValue(deviceId, out KnownPeer? existing) ? existing.FirstSeen : now;
            _peers[deviceId] = new KnownPeer(deviceId, normalized, deviceName ?? existing?.DeviceName, firstSeen, now);
            Save();
        }
    }

    /// <inheritdoc />
    public bool Forget(string deviceId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_peers.Remove(deviceId))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KnownPeer> GetAll()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _peers.Values.OrderBy(p => p.DeviceName ?? p.DeviceId, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            List<KnownPeer>? peers = JsonSerializer.Deserialize<List<KnownPeer>>(json, SerializerOptions);
            if (peers is null)
            {
                return;
            }

            foreach (KnownPeer peer in peers)
            {
                if (!string.IsNullOrWhiteSpace(peer.DeviceId) && !string.IsNullOrWhiteSpace(peer.Fingerprint))
                {
                    _peers[peer.DeviceId] = peer with { Fingerprint = CertificateManager.NormalizeFingerprint(peer.Fingerprint) };
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt pin file must not brick the app. Starting empty means the
            // next connection to each peer is a first use, which is exactly the
            // TOFU behaviour for an unknown peer.
        }
        catch (IOException)
        {
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(_peers.Values.ToList(), SerializerOptions);
        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
