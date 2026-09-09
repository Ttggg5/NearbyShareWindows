using NearbyShare.App.ViewModels;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Services;

namespace NearbyShare.App.Services;

/// <summary>
/// Keeps the TLS listener and mDNS advertising alive for the app's lifetime, and
/// bridges Core's background-thread events onto the UI thread.
/// </summary>
/// <remarks>
/// Shaped like an <c>IHostedService</c> (StartAsync/StopAsync) so it can be moved
/// onto a real generic host later without changing its callers. It is started
/// from <c>App.OnLaunched</c> rather than from a page, so a peer can send us
/// files whichever page is open.
/// </remarks>
public sealed class TransferListenerService
{
    private readonly INearbyShareService _shareService;
    private readonly IDialogService _dialogService;
    private readonly IUiDispatcher _dispatcher;
    private readonly ProgressViewModel _progress;

    private bool _started;

    public TransferListenerService(
        INearbyShareService shareService,
        IDialogService dialogService,
        IUiDispatcher dispatcher,
        ProgressViewModel progress)
    {
        _shareService = shareService;
        _dialogService = dialogService;
        _dispatcher = dispatcher;
        _progress = progress;
    }

    /// <summary>The most recent startup failure, surfaced on the device list page.</summary>
    public string? StartupError { get; private set; }

    /// <summary>Raised when startup fails, so the UI can show why discovery is dead.</summary>
    public event EventHandler<string>? StartupFailed;

    /// <summary>Starts listening and advertising. Safe to call more than once.</summary>
    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        _shareService.IncomingTransferRequested += OnIncomingTransferRequested;
        _shareService.IncomingProgressChanged += OnIncomingProgressChanged;
        _shareService.TransferStatusChanged += OnTransferStatusChanged;

        try
        {
            await _shareService.StartAsync();
        }
        catch (Exception ex)
        {
            // Most likely causes on a real machine: the mDNS UDP port is taken by
            // another responder, or Windows Firewall blocked the listener. The app
            // stays usable through the manual-IP path, so this is reported rather
            // than fatal.
            StartupError = ex.Message;
            _dispatcher.Post(() => StartupFailed?.Invoke(this, ex.Message));
        }
    }

    /// <summary>Stops listening and advertising.</summary>
    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        _shareService.IncomingTransferRequested -= OnIncomingTransferRequested;
        _shareService.IncomingProgressChanged -= OnIncomingProgressChanged;
        _shareService.TransferStatusChanged -= OnTransferStatusChanged;

        try
        {
            await _shareService.StopAsync();
        }
        catch (Exception)
        {
            // Shutdown races with window teardown; nothing useful to report.
        }
    }

    /// <summary>
    /// Answers an inbound <c>OFFER</c> by showing the accept/reject dialog.
    /// </summary>
    private void OnIncomingTransferRequested(object? sender, IncomingTransferEventArgs e)
    {
        // The Core session is awaiting the decision, so this must not block the
        // event handler; the dialog resolves it asynchronously.
        _ = Task.Run(async () =>
        {
            try
            {
                bool accepted = await _dialogService.ConfirmIncomingTransferAsync(e.Request);
                if (accepted)
                {
                    _dispatcher.Post(() => _progress.BeginIncoming(e.Request));
                    e.Accept();
                }
                else
                {
                    e.Reject();
                }
            }
            catch (Exception)
            {
                e.Reject();
            }
        });
    }

    private void OnIncomingProgressChanged(object? sender, TransferProgress progress) =>
        _dispatcher.Post(() => _progress.Update(progress));

    private void OnTransferStatusChanged(object? sender, TransferStatusEventArgs e) =>
        _dispatcher.Post(() => _progress.UpdateStatus(e));
}
