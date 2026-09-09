using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NearbyShare.App.Services;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Persistence;
using NearbyShare.Core.Services;

namespace NearbyShare.App.ViewModels;

/// <summary>
/// Backs <c>ProgressPage</c> and drives the active-transfer state shown there.
/// Registered as a DI singleton so both <see cref="DeviceListViewModel"/> (outbound
/// sends) and <see cref="TransferListenerService"/> (inbound receives) update the
/// same instance regardless of which page is currently visible.
/// </summary>
public partial class ProgressViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isActive;

    [ObservableProperty]
    private bool _isIncoming;

    [ObservableProperty]
    private string _peerDeviceName = string.Empty;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _overallFraction;

    [ObservableProperty]
    private double _fileFraction;

    [ObservableProperty]
    private string _bytesSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel;

    /// <summary>The last transfer's outcome, kept visible once <see cref="IsActive"/> goes false.</summary>
    [ObservableProperty]
    private string _lastResultText = string.Empty;

    /// <summary>True when nothing is transferring right now, i.e. <c>!IsActive</c>.</summary>
    public bool IsIdle => !IsActive;

    /// <summary>
    /// The token for the transfer currently in flight. Only meaningful after
    /// <see cref="BeginOutgoing"/>; <see cref="Cancel"/> is the only way it is
    /// signaled (Core has no separate cancellation hookup for inbound receives).
    /// </summary>
    public CancellationToken CancellationToken => (_cts ??= new CancellationTokenSource()).Token;

    /// <summary>Starts tracking an outbound send.</summary>
    public void BeginOutgoing(string peerDeviceName, IReadOnlyList<OutgoingFile> files)
    {
        ResetCts();
        IsActive = true;
        IsIncoming = false;
        PeerDeviceName = peerDeviceName;
        FileCount = files.Count;
        CurrentFileName = string.Empty;
        OverallFraction = 0;
        FileFraction = 0;
        BytesSummary = string.Empty;
        StatusText = $"Sending to {peerDeviceName}…";
        CanCancel = true;
    }

    /// <summary>Starts tracking an inbound receive, once the offer has been accepted.</summary>
    public void BeginIncoming(IncomingTransferRequest request)
    {
        ResetCts();
        IsActive = true;
        IsIncoming = true;
        PeerDeviceName = request.PeerDeviceName;
        FileCount = request.Files.Count;
        CurrentFileName = string.Empty;
        OverallFraction = 0;
        FileFraction = 0;
        BytesSummary = string.Empty;
        StatusText = $"Receiving from {request.PeerDeviceName}…";

        // Core's inbound receive loop (NearbyShareService.HandleInboundConnectionAsync)
        // is driven by the listener's own lifetime token, not one the UI can reach,
        // so there is nothing for a "cancel" button to signal here yet.
        CanCancel = false;
    }

    /// <summary>Applies a <see cref="TransferProgress"/> update from either direction.</summary>
    public void Update(TransferProgress progress)
    {
        IsActive = true;
        FileCount = progress.FileCount;
        CurrentFileName = progress.FileName;
        OverallFraction = progress.OverallFraction;
        FileFraction = progress.FileFraction;
        BytesSummary = $"{ByteFormatter.Format(progress.TotalBytesTransferred)} / {ByteFormatter.Format(progress.TotalBytes)}";
        StatusText = $"{(IsIncoming ? "Receiving" : "Sending")} file {progress.FileIndex + 1} of {progress.FileCount}: {progress.FileName}";
    }

    /// <summary>Applies a state-machine transition from <see cref="TransferSession.StateChanged"/>.</summary>
    public void UpdateStatus(TransferStatusEventArgs e)
    {
        IsIncoming = e.Direction == TransferDirection.Incoming;
        PeerDeviceName = e.PeerDeviceName;

        string phase = e.State switch
        {
            TransferState.Connected => "Connecting…",
            TransferState.HelloExchanged => "Handshake complete…",
            TransferState.Offered => IsIncoming
                ? "Offer received…"
                : "Waiting for the other device to accept…",
            TransferState.Accepted => "Starting transfer…",
            TransferState.Transferring => StatusText,
            TransferState.Rejected => "Declined by the peer.",
            TransferState.Completed => "Completed.",
            TransferState.Cancelled => "Cancelled.",
            TransferState.Failed => e.Error is not null ? $"Failed: {e.Error.Message}" : "Failed.",
            _ => StatusText,
        };

        StatusText = phase;

        bool terminal = e.State is TransferState.Completed
            or TransferState.Rejected
            or TransferState.Cancelled
            or TransferState.Failed;

        if (!terminal)
        {
            IsActive = true;
            return;
        }

        LastResultText = $"{e.PeerDeviceName}: {phase}";
        CanCancel = false;

        // Outgoing sends have DeviceListViewModel call Finish() once SendAsync
        // returns; inbound receives have no equivalent caller, so a terminal
        // state is where this instance's own "active" tracking has to end.
        if (IsIncoming)
        {
            IsActive = false;
        }
    }

    /// <summary>Ends tracking of the current transfer (called by the sender once <c>SendAsync</c> returns).</summary>
    public void Finish()
    {
        IsActive = false;
        CanCancel = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void ResetCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }
}
