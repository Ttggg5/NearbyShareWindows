using System.Net;
using System.Security.Cryptography.X509Certificates;
using NearbyShare.Core.Discovery;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Persistence;
using NearbyShare.Core.Protocol;

namespace NearbyShare.Core.Services;

/// <summary>Where the app keeps its persistent state, and how it behaves at runtime.</summary>
public sealed class NearbyShareOptions
{
    /// <summary>Folder holding the settings file, certificate and pinned-peer store.</summary>
    public required string DataFolder { get; init; }

    /// <summary>Folder received files are written to.</summary>
    public required string DownloadFolder { get; init; }

    /// <summary>TCP port to listen on. Zero lets the OS choose, which is the default.</summary>
    public int ListenPort { get; init; }

    /// <summary>The <c>os</c> value advertised over mDNS.</summary>
    public string Os { get; init; } = ProtocolConstants.OsWindows;

    /// <summary>Path of the PKCS#12 file holding this device's certificate.</summary>
    public string CertificatePath => Path.Combine(DataFolder, "device-certificate.pfx");

    /// <summary>Path of the pinned-peer store.</summary>
    public string KnownPeersPath => Path.Combine(DataFolder, "known-peers.json");

    /// <summary>Path of the settings file.</summary>
    public string SettingsPath => Path.Combine(DataFolder, "settings.json");
}

/// <summary>
/// Default <see cref="INearbyShareService"/>: owns the certificate, the
/// lifetime-long TLS listener, mDNS discovery, and per-transfer sessions.
/// </summary>
public sealed class NearbyShareService : INearbyShareService
{
    private readonly NearbyShareOptions _options;
    private readonly IDeviceIdentityProvider _identityProvider;
    private readonly IDiscoveryService _discovery;
    private readonly IKnownPeerStore _knownPeers;
    private readonly ITransferHistoryRepository _history;
    private readonly CertificateManager _certificateManager;
    private readonly TofuCertificateValidator _validator;

    private X509Certificate2? _certificate;
    private TlsListener? _listener;
    private TlsClient? _client;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _disposed;

    public NearbyShareService(
        NearbyShareOptions options,
        IDeviceIdentityProvider identityProvider,
        IDiscoveryService discovery,
        IKnownPeerStore knownPeers,
        ITransferHistoryRepository history)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _knownPeers = knownPeers ?? throw new ArgumentNullException(nameof(knownPeers));
        _history = history ?? throw new ArgumentNullException(nameof(history));

        _certificateManager = new CertificateManager(_options.CertificatePath);
        _validator = new TofuCertificateValidator(_knownPeers);

        _discovery.PeerChanged += (_, change) => PeerChanged?.Invoke(this, change);
    }

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public int ListenPort { get; private set; }

    /// <inheritdoc />
    public LocalIdentity Identity { get; private set; }

    /// <inheritdoc />
    public string CertificateFingerprint { get; private set; } = string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<PeerDevice> Peers => _discovery.Peers;

    /// <inheritdoc />
    public event EventHandler<PeerChange>? PeerChanged;

    /// <inheritdoc />
    public event EventHandler<IncomingTransferEventArgs>? IncomingTransferRequested;

    /// <inheritdoc />
    public event EventHandler<TransferProgress>? IncomingProgressChanged;

    /// <inheritdoc />
    public event EventHandler<TransferStatusEventArgs>? TransferStatusChanged;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(_options.DataFolder);

        Identity = await _identityProvider.GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        _certificate = _certificateManager.GetOrCreateCertificate();
        CertificateFingerprint = CertificateManager.ComputeFingerprint(_certificate);
        _client = new TlsClient(_certificate, _validator);

        _listener = new TlsListener(_certificate, IPAddress.Any, _options.ListenPort);
        _listener.Start();
        ListenPort = _listener.Port;

        _listenerCts = new CancellationTokenSource();
        _listenerTask = _listener.RunAsync(
            HandleInboundConnectionAsync,
            onError: _ => { /* per-connection failures are reported through TransferStatusChanged */ },
            _listenerCts.Token);

        await _discovery.StartAsync(
            new LocalDeviceAdvertisement(
                Identity.DeviceId,
                Identity.DeviceName,
                CertificateFingerprint,
                ListenPort,
                _options.Os),
            cancellationToken).ConfigureAwait(false);

        IsRunning = true;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;

        await _discovery.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_listenerCts is not null)
        {
            await _listenerCts.CancelAsync().ConfigureAwait(false);
        }

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _listenerTask = null;
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
    }

    /// <inheritdoc />
    public async Task SetDeviceNameAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        await _identityProvider.SetDeviceNameAsync(deviceName, cancellationToken).ConfigureAwait(false);
        Identity = await _identityProvider.GetIdentityAsync(cancellationToken).ConfigureAwait(false);

        if (IsRunning)
        {
            await _discovery.UpdateAdvertisementAsync(
                new LocalDeviceAdvertisement(
                    Identity.DeviceId,
                    Identity.DeviceName,
                    CertificateFingerprint,
                    ListenPort,
                    _options.Os),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task RefreshPeersAsync(CancellationToken cancellationToken = default) =>
        _discovery.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> SendFilesAsync(
        PeerDevice peer,
        IReadOnlyList<OutgoingFile> files,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return SendCoreAsync(
            peer.EndPoint,
            string.IsNullOrEmpty(peer.DeviceId) ? null : peer.DeviceId,
            string.IsNullOrEmpty(peer.Fingerprint) ? null : peer.Fingerprint,
            peer.DeviceName,
            files,
            progress,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SendFilesToAddressAsync(
        IPEndPoint endPoint,
        IReadOnlyList<OutgoingFile> files,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        // No mDNS record means no advertised fingerprint and no expected device
        // id, so TOFU falls back to plain first-use pinning against whatever the
        // peer presents. This is why manual entry is a debug path.
        return SendCoreAsync(endPoint, null, null, endPoint.ToString(), files, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<KnownPeer> GetKnownPeers() => _knownPeers.GetAll();

    /// <inheritdoc />
    public bool ForgetPeer(string deviceId) => _knownPeers.Forget(deviceId);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await _discovery.DisposeAsync().ConfigureAwait(false);
        _certificateManager.Dispose();
    }

    private async Task<bool> SendCoreAsync(
        IPEndPoint endPoint,
        string? expectedDeviceId,
        string? advertisedFingerprint,
        string peerDisplayName,
        IReadOnlyList<OutgoingFile> files,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("StartAsync must be called before sending files.");
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long totalBytes = files.Sum(f => f.Size);
        long lastBytes = 0;

        var relay = new Progress<TransferProgress>(p =>
        {
            lastBytes = p.TotalBytesTransferred;
            progress?.Report(p);
        });

        await using TlsConnection connection = await _client
            .ConnectAsync(endPoint, expectedDeviceId, advertisedFingerprint, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var session = new TransferSession(connection.Stream, Identity, _validator, connection.RemoteCertificateFingerprint);
        session.StateChanged += (_, state) => TransferStatusChanged?.Invoke(
            this,
            new TransferStatusEventArgs(session.TransferId ?? string.Empty, TransferDirection.Outgoing, state, peerDisplayName, session.FailureReason));

        try
        {
            bool accepted = await session.SendAsync(files, expectedDeviceId, relay, cancellationToken).ConfigureAwait(false);
            await RecordHistoryAsync(session, TransferDirection.Outgoing, peerDisplayName, files.Select(f => f.Name).ToList(),
                totalBytes, lastBytes, startedAt, accepted ? TransferOutcome.Completed : TransferOutcome.Rejected, null)
                .ConfigureAwait(false);
            return accepted;
        }
        catch (OperationCanceledException)
        {
            await RecordHistoryAsync(session, TransferDirection.Outgoing, peerDisplayName, files.Select(f => f.Name).ToList(),
                totalBytes, lastBytes, startedAt, TransferOutcome.Cancelled, ErrorCodes.Cancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await RecordHistoryAsync(session, TransferDirection.Outgoing, peerDisplayName, files.Select(f => f.Name).ToList(),
                totalBytes, lastBytes, startedAt, TransferOutcome.Failed,
                (ex as ProtocolException)?.ErrorCode ?? ErrorCodes.InternalError).ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandleInboundConnectionAsync(TlsConnection connection, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long lastBytes = 0;
        var fileNames = new List<string>();
        long totalBytes = 0;
        string peerName = connection.RemoteEndPoint?.ToString() ?? "unknown";

        var handler = new FolderIncomingTransferHandler(
            _options.DownloadFolder,
            async (request, ct) =>
            {
                peerName = request.PeerDeviceName;
                fileNames = request.Files.Select(f => f.Name).ToList();
                totalBytes = request.TotalBytes;

                EventHandler<IncomingTransferEventArgs>? subscribers = IncomingTransferRequested;
                if (subscribers is null)
                {
                    // Nothing is listening (no UI up yet); declining is the safe default.
                    return false;
                }

                var args = new IncomingTransferEventArgs(request);
                subscribers.Invoke(this, args);

                using CancellationTokenRegistration registration = ct.Register(args.RejectIfUndecided);
                return await args.DecisionTask.ConfigureAwait(false);
            });

        var progress = new Progress<TransferProgress>(p =>
        {
            lastBytes = p.TotalBytesTransferred;
            IncomingProgressChanged?.Invoke(this, p);
        });

        var session = new TransferSession(connection.Stream, Identity, _validator, connection.RemoteCertificateFingerprint);
        session.StateChanged += (_, state) => TransferStatusChanged?.Invoke(
            this,
            new TransferStatusEventArgs(session.TransferId ?? string.Empty, TransferDirection.Incoming, state, peerName, session.FailureReason));

        try
        {
            bool received = await session.ReceiveAsync(handler, progress, cancellationToken).ConfigureAwait(false);
            await RecordHistoryAsync(session, TransferDirection.Incoming, peerName, fileNames, totalBytes, lastBytes,
                startedAt, received ? TransferOutcome.Completed : TransferOutcome.Rejected, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RecordHistoryAsync(session, TransferDirection.Incoming, peerName, fileNames, totalBytes, lastBytes,
                startedAt, TransferOutcome.Cancelled, ErrorCodes.Cancelled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RecordHistoryAsync(session, TransferDirection.Incoming, peerName, fileNames, totalBytes, lastBytes,
                startedAt, TransferOutcome.Failed, (ex as ProtocolException)?.ErrorCode ?? ErrorCodes.InternalError)
                .ConfigureAwait(false);

            TransferStatusChanged?.Invoke(
                this,
                new TransferStatusEventArgs(session.TransferId ?? string.Empty, TransferDirection.Incoming, TransferState.Failed, peerName, ex));
        }
    }

    private async Task RecordHistoryAsync(
        TransferSession session,
        TransferDirection direction,
        string peerName,
        IReadOnlyList<string> fileNames,
        long totalBytes,
        long bytesTransferred,
        DateTimeOffset startedAt,
        TransferOutcome outcome,
        string? errorCode)
    {
        try
        {
            await _history.AddAsync(
                new TransferHistoryEntry(
                    session.TransferId ?? Guid.NewGuid().ToString(),
                    direction,
                    session.PeerDeviceId ?? string.Empty,
                    session.PeerDeviceName ?? peerName,
                    fileNames,
                    totalBytes,
                    bytesTransferred,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    outcome,
                    errorCode)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // History is a best-effort side channel; never fail a transfer over it.
        }
    }
}
