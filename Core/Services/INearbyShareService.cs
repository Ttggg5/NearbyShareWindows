using System.Net;
using NearbyShare.Core.Discovery;
using NearbyShare.Core.Networking;

namespace NearbyShare.Core.Services;

/// <summary>
/// An inbound offer surfaced to the UI, together with the reply the UI must make.
/// </summary>
public sealed class IncomingTransferEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IncomingTransferEventArgs(IncomingTransferRequest request)
    {
        Request = request;
    }

    /// <summary>What the peer is offering.</summary>
    public IncomingTransferRequest Request { get; }

    /// <summary>Accepts the offer. Exactly one of Accept/Reject must be called.</summary>
    public void Accept() => _decision.TrySetResult(true);

    /// <summary>Rejects the offer.</summary>
    public void Reject() => _decision.TrySetResult(false);

    /// <summary>Awaited by the transfer session for the user's decision.</summary>
    internal Task<bool> DecisionTask => _decision.Task;

    /// <summary>Defaults to rejection if no handler answered.</summary>
    internal void RejectIfUndecided() => _decision.TrySetResult(false);
}

/// <summary>Reports the lifecycle of a transfer to the UI.</summary>
public sealed class TransferStatusEventArgs : EventArgs
{
    public TransferStatusEventArgs(
        string transferId,
        Persistence.TransferDirection direction,
        TransferState state,
        string peerDeviceName,
        Exception? error = null)
    {
        TransferId = transferId;
        Direction = direction;
        State = state;
        PeerDeviceName = peerDeviceName;
        Error = error;
    }

    public string TransferId { get; }

    public Persistence.TransferDirection Direction { get; }

    public TransferState State { get; }

    public string PeerDeviceName { get; }

    public Exception? Error { get; }
}

/// <summary>
/// The single Core-level façade the app's ViewModels talk to: it owns the
/// certificate, the TLS listener, mDNS discovery, and the transfer sessions.
/// ViewModels depend on this interface only, never on concrete networking or
/// storage types.
/// </summary>
public interface INearbyShareService : IAsyncDisposable
{
    /// <summary>True once the listener and discovery are live.</summary>
    bool IsRunning { get; }

    /// <summary>The TCP port advertised over mDNS. Zero until started.</summary>
    int ListenPort { get; }

    /// <summary>This device's id and name.</summary>
    LocalIdentity Identity { get; }

    /// <summary>The SHA-256 fingerprint this device advertises as the TXT <c>fp</c> field.</summary>
    string CertificateFingerprint { get; }

    /// <summary>Peers currently visible over mDNS.</summary>
    IReadOnlyList<PeerDevice> Peers { get; }

    /// <summary>Raised whenever the peer list changes.</summary>
    event EventHandler<PeerChange>? PeerChanged;

    /// <summary>
    /// Raised when a peer offers files. A handler must call
    /// <see cref="IncomingTransferEventArgs.Accept"/> or
    /// <see cref="IncomingTransferEventArgs.Reject"/>; with no handler the offer is
    /// rejected.
    /// </summary>
    event EventHandler<IncomingTransferEventArgs>? IncomingTransferRequested;

    /// <summary>Raised as inbound transfers progress.</summary>
    event EventHandler<TransferProgress>? IncomingProgressChanged;

    /// <summary>Raised on inbound transfer state changes.</summary>
    event EventHandler<TransferStatusEventArgs>? TransferStatusChanged;

    /// <summary>
    /// Starts the TLS listener and mDNS advertising/browsing. Safe to call once at
    /// app launch; the listener then runs for the app's lifetime.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the listener and discovery.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames this device and re-advertises under the new name.</summary>
    Task SetDeviceNameAsync(string deviceName, CancellationToken cancellationToken = default);

    /// <summary>Re-issues the mDNS browse query.</summary>
    Task RefreshPeersAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends files to a discovered peer.</summary>
    /// <returns>True if the peer accepted and every file was sent.</returns>
    Task<bool> SendFilesAsync(
        PeerDevice peer,
        IReadOnlyList<OutgoingFile> files,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends files to a manually entered address, bypassing mDNS. The debug/testing
    /// path that makes an end-to-end transfer possible before discovery is proven
    /// working across devices.
    /// </summary>
    Task<bool> SendFilesToAddressAsync(
        IPEndPoint endPoint,
        IReadOnlyList<OutgoingFile> files,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Peers whose certificate fingerprints have been pinned.</summary>
    IReadOnlyList<KnownPeer> GetKnownPeers();

    /// <summary>
    /// Forgets a pinned peer, so its next connection is treated as a first use.
    /// The recovery path after a legitimate identity change (app reinstalled on
    /// the peer), which PROTOCOL.md §2 step 4 requires be an explicit user action.
    /// </summary>
    bool ForgetPeer(string deviceId);
}
