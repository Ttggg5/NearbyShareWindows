using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Makaretu.Dns;
using NearbyShare.Core.Protocol;
using MdnsMessage = Makaretu.Dns.Message;

namespace NearbyShare.Core.Discovery;

/// <summary>
/// mDNS/DNS-SD implementation of <see cref="IDiscoveryService"/> for
/// <c>_nearbyshare._tcp.local.</c>, built on <c>Makaretu.Dns.Multicast</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Makaretu.Dns.Multicast</c> was chosen because it is the only actively
/// published .NET mDNS package that can both <em>advertise</em> a service with
/// TXT records and <em>browse</em> for peers. <c>Zeroconf</c> and
/// <c>Tmds.MDns</c> are browse-only, which cannot satisfy PROTOCOL.md §1.
/// </para>
/// <para>
/// Records are assembled from raw <c>AnswerReceived</c> traffic rather than the
/// library's higher-level instance-discovered event, because that event carries
/// only the instance name in this package version — the SRV/TXT/A records have to
/// be correlated by name anyway.
/// </para>
/// <para>
/// Known limitations, to verify on real hardware (see TESTING.md): Windows
/// Firewall must allow inbound UDP/5353; a machine already running Bonjour or a
/// conflicting mDNS responder may bind the port first; and multi-homed hosts
/// (VPN/Hyper-V/WSL adapters) can advertise unreachable addresses, which is why
/// the app also ships a manual IP-entry path.
/// </para>
/// </remarks>
public sealed class MdnsDiscoveryService : IDiscoveryService
{
    private static readonly DomainName ServiceName = new(ProtocolConstants.ServiceType);
    private static readonly TimeSpan RequeryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, InstanceRecords> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerDevice> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Channel<PeerChange>> _watchers = new();

    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private LocalDeviceAdvertisement _advertisement;
    private Timer? _maintenanceTimer;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<PeerDevice> Peers
    {
        get
        {
            lock (_gate)
            {
                return _peers.Values.OrderBy(p => p.DeviceName, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<PeerChange>? PeerChanged;

    /// <inheritdoc />
    public Task StartAsync(LocalDeviceAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return UpdateAdvertisementAsync(advertisement, cancellationToken);
        }

        _advertisement = advertisement;

        var mdns = new MulticastService { IgnoreDuplicateMessages = true };
        var discovery = new ServiceDiscovery(mdns);

        mdns.NetworkInterfaceDiscovered += OnNetworkInterfaceDiscovered;
        mdns.AnswerReceived += OnAnswerReceived;
        discovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;

        _mdns = mdns;
        _discovery = discovery;

        _profile = BuildProfile(advertisement);
        discovery.Advertise(_profile);

        mdns.Start();
        discovery.Announce(_profile);
        discovery.QueryServiceInstances(ServiceName);

        _maintenanceTimer = new Timer(_ => RunMaintenance(), null, RequeryInterval, RequeryInterval);
        IsRunning = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAdvertisementAsync(LocalDeviceAdvertisement advertisement, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _advertisement = advertisement;
        if (_discovery is null || _mdns is null)
        {
            return Task.CompletedTask;
        }

        // Withdraw the old instance name before publishing the new one, otherwise
        // peers keep showing the stale name until its TTL expires.
        if (_profile is not null)
        {
            try
            {
                _discovery.Unadvertise(_profile);
            }
            catch (Exception)
            {
                // A goodbye packet is best-effort; a failure here must not stop
                // re-advertising under the new name.
            }
        }

        _profile = BuildProfile(advertisement);
        _discovery.Advertise(_profile);
        _discovery.Announce(_profile);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _discovery?.QueryServiceInstances(ServiceName);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        IsRunning = false;

        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;

        if (_mdns is not null)
        {
            _mdns.NetworkInterfaceDiscovered -= OnNetworkInterfaceDiscovered;
            _mdns.AnswerReceived -= OnAnswerReceived;
        }

        if (_discovery is not null)
        {
            _discovery.ServiceInstanceShutdown -= OnServiceInstanceShutdown;
            try
            {
                _discovery.Unadvertise();
            }
            catch (Exception)
            {
                // Best-effort goodbye.
            }

            _discovery.Dispose();
            _discovery = null;
        }

        if (_mdns is not null)
        {
            _mdns.Stop();
            _mdns.Dispose();
            _mdns = null;
        }

        _profile = null;

        lock (_gate)
        {
            _instances.Clear();
            _hostAddresses.Clear();
            _peers.Clear();
        }

        foreach (Channel<PeerChange> channel in _watchers.Values)
        {
            channel.Writer.TryComplete();
        }

        _watchers.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PeerChange> WatchPeersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<PeerChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Guid.NewGuid();
        _watchers[id] = channel;

        try
        {
            // Replay the current list so a subscriber never misses peers that were
            // already visible when it subscribed.
            foreach (PeerDevice peer in Peers)
            {
                yield return new PeerChange(PeerChangeKind.Added, peer);
            }

            await foreach (PeerChange change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            _watchers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Advertising
    // ------------------------------------------------------------------

    private static ServiceProfile BuildProfile(LocalDeviceAdvertisement advertisement)
    {
        string instanceName = DnsSdNaming.SanitizeInstanceName(advertisement.DeviceName);
        var profile = new ServiceProfile(
            new DomainName(instanceName),
            ServiceName,
            (ushort)advertisement.Port);

        // TXT fields, exactly as specified by PROTOCOL.md §1.
        AddTxtProperty(profile, "v", ProtocolConstants.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddTxtProperty(profile, "id", advertisement.DeviceId);
        AddTxtProperty(profile, "fp", advertisement.Fingerprint);
        AddTxtProperty(profile, "port", advertisement.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddTxtProperty(profile, "os", advertisement.Os);
        return profile;
    }

    private static void AddTxtProperty(ServiceProfile profile, string key, string value)
    {
        if (!DnsSdNaming.IsTxtEntryWithinLimit(key, value))
        {
            throw new ArgumentException(
                $"TXT entry '{key}' exceeds the {DnsSdNaming.MaxTxtEntryBytes}-byte DNS-SD limit required by PROTOCOL.md §1.",
                nameof(value));
        }

        profile.AddProperty(key, value);
    }

    // ------------------------------------------------------------------
    // Browsing
    // ------------------------------------------------------------------

    private void OnNetworkInterfaceDiscovered(object? sender, NetworkInterfaceEventArgs e)
    {
        // A new interface (Wi-Fi reconnect, VPN up) means our announcement and our
        // browse query both need to go out again on it.
        try
        {
            if (_profile is not null)
            {
                _discovery?.Announce(_profile);
            }

            _discovery?.QueryServiceInstances(ServiceName);
        }
        catch (Exception)
        {
            // Interface churn races with shutdown; never let it escape onto the
            // library's background thread.
        }
    }

    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        string instance = Normalize(e.ServiceInstanceName.ToString());
        PeerDevice? removed = null;

        lock (_gate)
        {
            if (_instances.Remove(instance, out InstanceRecords? records) && records.DeviceId is not null)
            {
                if (_peers.Remove(records.DeviceId, out PeerDevice? peer))
                {
                    removed = peer;
                }
            }
        }

        if (removed is not null)
        {
            Publish(new PeerChange(PeerChangeKind.Removed, removed));
        }
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        try
        {
            IngestRecords(e.Message);
        }
        catch (Exception)
        {
            // A malformed or hostile mDNS packet must never take down the
            // library's receive loop.
        }
    }

    private void IngestRecords(MdnsMessage message)
    {
        var touchedInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingHostQueries = new List<DomainName>();

        lock (_gate)
        {
            foreach (ResourceRecord record in message.Answers.Concat(message.AdditionalRecords))
            {
                switch (record)
                {
                    case PTRRecord ptr when IsOurServiceName(ptr.Name):
                    {
                        string instance = Normalize(ptr.DomainName.ToString());
                        GetOrAdd(instance);
                        touchedInstances.Add(instance);

                        // We have the instance name but not yet its SRV/TXT.
                        pendingHostQueries.Add(ptr.DomainName);
                        break;
                    }

                    case SRVRecord srv when IsOurInstanceName(srv.Name):
                    {
                        string instance = Normalize(srv.Name.ToString());
                        InstanceRecords records = GetOrAdd(instance);
                        records.Port = srv.Port;
                        records.HostName = Normalize(srv.Target.ToString());
                        records.LastSeen = DateTimeOffset.UtcNow;
                        touchedInstances.Add(instance);

                        if (!_hostAddresses.ContainsKey(records.HostName))
                        {
                            pendingHostQueries.Add(srv.Target);
                        }

                        break;
                    }

                    case TXTRecord txt when IsOurInstanceName(txt.Name):
                    {
                        string instance = Normalize(txt.Name.ToString());
                        InstanceRecords records = GetOrAdd(instance);
                        foreach (string entry in txt.Strings)
                        {
                            if (DnsSdNaming.TryParseTxtEntry(entry, out string key, out string value))
                            {
                                records.Txt[key] = value;
                            }
                        }

                        records.LastSeen = DateTimeOffset.UtcNow;
                        touchedInstances.Add(instance);
                        break;
                    }

                    case AddressRecord address:
                    {
                        string host = Normalize(address.Name.ToString());

                        // Prefer IPv4: link-local IPv6 needs a scope id to connect
                        // to and is a common source of "connection refused" on
                        // multi-homed Windows hosts.
                        if (!_hostAddresses.TryGetValue(host, out IPAddress? existing) ||
                            (existing.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
                             address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                        {
                            _hostAddresses[host] = address.Address;
                        }

                        foreach (KeyValuePair<string, InstanceRecords> pair in _instances)
                        {
                            if (string.Equals(pair.Value.HostName, host, StringComparison.OrdinalIgnoreCase))
                            {
                                touchedInstances.Add(pair.Key);
                            }
                        }

                        break;
                    }
                }
            }
        }

        foreach (DomainName name in pendingHostQueries.Distinct())
        {
            SendResolveQueries(name);
        }

        foreach (string instance in touchedInstances)
        {
            TryMaterializePeer(instance);
        }
    }

    private void SendResolveQueries(DomainName name)
    {
        MulticastService? mdns = _mdns;
        if (mdns is null)
        {
            return;
        }

        try
        {
            mdns.SendQuery(name, type: DnsType.SRV);
            mdns.SendQuery(name, type: DnsType.TXT);
            mdns.SendQuery(name, type: DnsType.A);
        }
        catch (Exception)
        {
            // The socket may already be closing.
        }
    }

    private void TryMaterializePeer(string instance)
    {
        PeerChange? change = null;

        lock (_gate)
        {
            if (!_instances.TryGetValue(instance, out InstanceRecords? records))
            {
                return;
            }

            if (!records.Txt.TryGetValue("id", out string? deviceId) || string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            // Never list ourselves: we see our own announcements on the loopback
            // path of the multicast group.
            if (string.Equals(deviceId, _advertisement.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            records.Txt.TryGetValue("fp", out string? fingerprint);
            records.Txt.TryGetValue("os", out string? os);
            records.Txt.TryGetValue("v", out string? versionText);
            records.Txt.TryGetValue("port", out string? txtPort);

            int port = records.Port ?? 0;
            if (port == 0 && int.TryParse(txtPort, out int parsedPort))
            {
                port = parsedPort;
            }

            if (port <= 0 || port > 65535)
            {
                return;
            }

            IPAddress? address = null;
            if (records.HostName is not null)
            {
                _hostAddresses.TryGetValue(records.HostName, out address);
            }

            if (address is null)
            {
                return;
            }

            records.DeviceId = deviceId;

            var peer = new PeerDevice
            {
                DeviceId = deviceId,
                DeviceName = InstanceDisplayName(instance),
                Fingerprint = (fingerprint ?? string.Empty).Trim().ToLowerInvariant(),
                Address = address,
                Port = port,
                Os = os ?? string.Empty,
                ProtocolVersion = int.TryParse(versionText, out int version) ? version : ProtocolConstants.Version,
                LastSeen = DateTimeOffset.UtcNow,
                InstanceName = instance,
            };

            if (_peers.TryGetValue(deviceId, out PeerDevice? existing))
            {
                _peers[deviceId] = peer;
                bool materiallyChanged =
                    !existing.Address.Equals(peer.Address) ||
                    existing.Port != peer.Port ||
                    !string.Equals(existing.Fingerprint, peer.Fingerprint, StringComparison.Ordinal) ||
                    !string.Equals(existing.DeviceName, peer.DeviceName, StringComparison.Ordinal);

                if (materiallyChanged)
                {
                    change = new PeerChange(PeerChangeKind.Updated, peer);
                }
            }
            else
            {
                _peers[deviceId] = peer;
                change = new PeerChange(PeerChangeKind.Added, peer);
            }
        }

        if (change.HasValue)
        {
            Publish(change.Value);
        }
    }

    private void RunMaintenance()
    {
        try
        {
            _discovery?.QueryServiceInstances(ServiceName);
        }
        catch (Exception)
        {
            // Ignore: the service may be stopping.
        }

        var expired = new List<PeerDevice>();
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - PeerExpiry;

        lock (_gate)
        {
            foreach (KeyValuePair<string, InstanceRecords> pair in _instances.ToList())
            {
                if (pair.Value.LastSeen >= cutoff)
                {
                    continue;
                }

                _instances.Remove(pair.Key);
                if (pair.Value.DeviceId is not null && _peers.Remove(pair.Value.DeviceId, out PeerDevice? peer))
                {
                    expired.Add(peer);
                }
            }
        }

        foreach (PeerDevice peer in expired)
        {
            Publish(new PeerChange(PeerChangeKind.Removed, peer));
        }
    }

    private void Publish(PeerChange change)
    {
        PeerChanged?.Invoke(this, change);
        foreach (Channel<PeerChange> channel in _watchers.Values)
        {
            channel.Writer.TryWrite(change);
        }
    }

    private InstanceRecords GetOrAdd(string instance)
    {
        if (!_instances.TryGetValue(instance, out InstanceRecords? records))
        {
            records = new InstanceRecords();
            _instances[instance] = records;
        }

        records.LastSeen = DateTimeOffset.UtcNow;
        return records;
    }

    private static bool IsOurServiceName(DomainName name) =>
        Normalize(name.ToString()).Equals(Normalize(ProtocolConstants.ServiceTypeFullyQualified), StringComparison.OrdinalIgnoreCase);

    private static bool IsOurInstanceName(DomainName name) =>
        Normalize(name.ToString()).EndsWith("." + Normalize(ProtocolConstants.ServiceTypeFullyQualified), StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips the trailing dot so names compare consistently.</summary>
    private static string Normalize(string name) => name.TrimEnd('.');

    /// <summary>The instance label, i.e. the peer's display name without the service suffix.</summary>
    private static string InstanceDisplayName(string instance)
    {
        string suffix = "." + Normalize(ProtocolConstants.ServiceTypeFullyQualified);
        return instance.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? instance[..^suffix.Length]
            : instance;
    }

    private sealed class InstanceRecords
    {
        public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int? Port { get; set; }

        public string? HostName { get; set; }

        public string? DeviceId { get; set; }

        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    }
}
