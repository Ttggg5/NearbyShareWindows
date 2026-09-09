using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NearbyShare.App.Services;
using NearbyShare.Core.Discovery;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Services;

namespace NearbyShare.App.ViewModels;

/// <summary>
/// Backs <c>DeviceListPage</c>: the discovered-peer list, the file picker, and
/// the manual-IP fallback used to test a transfer before mDNS discovery is proven
/// working across devices.
/// </summary>
public partial class DeviceListViewModel : ObservableObject
{
    private readonly INearbyShareService _shareService;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ProgressViewModel _progress;

    [ObservableProperty]
    private string _statusMessage = "Starting…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToSelectedPeerCommand))]
    private PeerItemViewModel? _selectedPeer;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Manual-entry host, for the debug/testing path.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToManualAddressCommand))]
    private string _manualHost = string.Empty;

    /// <summary>Manual-entry port, for the debug/testing path.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToManualAddressCommand))]
    private string _manualPort = string.Empty;

    [ObservableProperty]
    private string _localIdentitySummary = string.Empty;

    public DeviceListViewModel(
        INearbyShareService shareService,
        IFilePickerService filePicker,
        IDialogService dialogs,
        IUiDispatcher dispatcher,
        ProgressViewModel progress)
    {
        _shareService = shareService;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _progress = progress;

        _shareService.PeerChanged += OnPeerChanged;
    }

    /// <summary>The peers currently visible over mDNS.</summary>
    public ObservableCollection<PeerItemViewModel> Peers { get; } = new();

    /// <summary>True when discovery has found nobody yet, so the page can explain what to check.</summary>
    public bool HasNoPeers => Peers.Count == 0;

    /// <summary>Called when the page appears: sync the list with whatever discovery already found.</summary>
    public void OnNavigatedTo()
    {
        RebuildPeerList();
        UpdateStatus();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await _shareService.RefreshPeersAsync();
            RebuildPeerList();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not refresh: {ex.Message}";
        }
    }

    private bool CanSendToSelectedPeer() => SelectedPeer is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSendToSelectedPeer))]
    private async Task SendToSelectedPeerAsync()
    {
        PeerItemViewModel? target = SelectedPeer;
        if (target is null)
        {
            return;
        }

        if (!target.IsCompatible)
        {
            await _dialogs.ShowMessageAsync("Incompatible device", target.IncompatibilityWarning!);
            return;
        }

        IReadOnlyList<OutgoingFile>? files = await PickFilesAsync();
        if (files is null)
        {
            return;
        }

        await SendAsync(
            target.DeviceName,
            files,
            (progress, cancellationToken) => _shareService.SendFilesAsync(target.Peer, files, progress, cancellationToken),
            target.DeviceId);
    }

    private bool CanSendToManualAddress() =>
        !IsBusy && IPAddress.TryParse(ManualHost.Trim(), out _) &&
        int.TryParse(ManualPort.Trim(), out int port) && port is > 0 and <= 65535;

    /// <summary>
    /// The debug/testing path: connect straight to an address the user typed,
    /// skipping mDNS entirely. Lets an end-to-end transfer be tested before
    /// discovery is confirmed working across devices.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendToManualAddress))]
    private async Task SendToManualAddressAsync()
    {
        if (!IPAddress.TryParse(ManualHost.Trim(), out IPAddress? address) ||
            !int.TryParse(ManualPort.Trim(), out int port))
        {
            return;
        }

        IReadOnlyList<OutgoingFile>? files = await PickFilesAsync();
        if (files is null)
        {
            return;
        }

        var endPoint = new IPEndPoint(address, port);
        await SendAsync(
            endPoint.ToString(),
            files,
            (progress, cancellationToken) => _shareService.SendFilesToAddressAsync(endPoint, files, progress, cancellationToken),
            expectedDeviceId: null);
    }

    private async Task<IReadOnlyList<OutgoingFile>?> PickFilesAsync()
    {
        IReadOnlyList<string> paths = await _filePicker.PickFilesToSendAsync();
        if (paths.Count == 0)
        {
            return null;
        }

        var files = new List<OutgoingFile>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                files.Add(OutgoingFile.FromPath(path));
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync("Could not read file", $"{path}\n\n{ex.Message}");
                return null;
            }
        }

        return files;
    }

    private async Task SendAsync(
        string peerDisplayName,
        IReadOnlyList<OutgoingFile> files,
        Func<IProgress<TransferProgress>, CancellationToken, Task<bool>> send,
        string? expectedDeviceId)
    {
        IsBusy = true;
        SendToSelectedPeerCommand.NotifyCanExecuteChanged();
        SendToManualAddressCommand.NotifyCanExecuteChanged();

        try
        {
            _progress.BeginOutgoing(peerDisplayName, files);

            var relay = new Progress<TransferProgress>(p => _dispatcher.Post(() => _progress.Update(p)));
            bool accepted = await send(relay, _progress.CancellationToken);

            StatusMessage = accepted
                ? $"Sent {files.Count} file(s) to {peerDisplayName}."
                : $"{peerDisplayName} declined the transfer.";
        }
        catch (PeerIdentityChangedException ex)
        {
            // PROTOCOL.md §2 step 4: this is a security event, not a plain error.
            StatusMessage = $"{peerDisplayName}: identity changed. Transfer aborted.";

            bool forget = await _dialogs.ConfirmForgetPeerAsync(
                peerDisplayName,
                $"The device presented certificate {ex.PresentedFingerprint}, but {ex.ExpectedFingerprint} was saved for it previously.");

            if (forget && !string.IsNullOrEmpty(expectedDeviceId))
            {
                _shareService.ForgetPeer(expectedDeviceId);
                StatusMessage = $"Forgot the saved identity for {peerDisplayName}. Try sending again.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transfer cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Transfer failed: {ex.Message}";
            await _dialogs.ShowMessageAsync("Transfer failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            _progress.Finish();
            SendToSelectedPeerCommand.NotifyCanExecuteChanged();
            SendToManualAddressCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnPeerChanged(object? sender, PeerChange change) => _dispatcher.Post(() =>
    {
        switch (change.Kind)
        {
            case PeerChangeKind.Added:
            case PeerChangeKind.Updated:
            {
                PeerItemViewModel? existing = Peers.FirstOrDefault(p => p.DeviceId == change.Peer.DeviceId);
                if (existing is null)
                {
                    Peers.Add(new PeerItemViewModel(change.Peer));
                }
                else
                {
                    existing.UpdateFrom(change.Peer);
                }

                break;
            }

            case PeerChangeKind.Removed:
            {
                PeerItemViewModel? existing = Peers.FirstOrDefault(p => p.DeviceId == change.Peer.DeviceId);
                if (existing is not null)
                {
                    Peers.Remove(existing);
                    if (ReferenceEquals(SelectedPeer, existing))
                    {
                        SelectedPeer = null;
                    }
                }

                break;
            }
        }

        OnPropertyChanged(nameof(HasNoPeers));
        UpdateStatus();
    });

    private void RebuildPeerList()
    {
        Peers.Clear();
        foreach (PeerDevice peer in _shareService.Peers)
        {
            Peers.Add(new PeerItemViewModel(peer));
        }

        OnPropertyChanged(nameof(HasNoPeers));
    }

    private void UpdateStatus()
    {
        if (!_shareService.IsRunning)
        {
            StatusMessage = "Not listening. Check that Windows Firewall allows this app on the private network.";
            LocalIdentitySummary = string.Empty;
            return;
        }

        LocalIdentitySummary =
            $"{_shareService.Identity.DeviceName} · port {_shareService.ListenPort} · " +
            $"fingerprint {Truncate(_shareService.CertificateFingerprint)}";

        StatusMessage = Peers.Count == 0
            ? "Looking for devices on this network…"
            : $"{Peers.Count} device(s) found.";
    }

    private static string Truncate(string fingerprint) =>
        fingerprint.Length >= 8 ? fingerprint[..8] : fingerprint;
}
