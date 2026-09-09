using Microsoft.UI.Xaml.Controls;
using NearbyShare.App.Views;
using NearbyShare.Core.Networking;

namespace NearbyShare.App.Services;

/// <summary>
/// Shows the app's dialogs. An interface so ViewModels can ask for a decision
/// without referencing <c>ContentDialog</c>.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows the accept/reject prompt for an inbound <c>OFFER</c>.</summary>
    Task<bool> ConfirmIncomingTransferAsync(IncomingTransferRequest request);

    /// <summary>Shows a plain informational message.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Shows the identity-change warning from PROTOCOL.md §2 step 4 and asks
    /// whether to forget the old pin.
    /// </summary>
    Task<bool> ConfirmForgetPeerAsync(string deviceName, string details);
}

/// <inheritdoc />
public sealed class DialogService : IDialogService
{
    private readonly IWindowContext _windowContext;
    private readonly IUiDispatcher _dispatcher;

    // WinUI 3 throws if a second ContentDialog is shown while one is open, and
    // two peers can offer files at the same moment.
    private readonly SemaphoreSlim _dialogMutex = new(1, 1);

    public DialogService(IWindowContext windowContext, IUiDispatcher dispatcher)
    {
        _windowContext = windowContext;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmIncomingTransferAsync(IncomingTransferRequest request)
    {
        await _dialogMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ShowOnUiThreadAsync(async () =>
            {
                if (_windowContext.XamlRoot is null)
                {
                    // No UI is up yet; declining is the safe default.
                    return false;
                }

                var dialog = new IncomingTransferDialog(request) { XamlRoot = _windowContext.XamlRoot };
                ContentDialogResult result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary;
            }).ConfigureAwait(false);
        }
        finally
        {
            _dialogMutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task ShowMessageAsync(string title, string message)
    {
        await _dialogMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            await ShowOnUiThreadAsync(async () =>
            {
                if (_windowContext.XamlRoot is null)
                {
                    return false;
                }

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = _windowContext.XamlRoot,
                };

                await dialog.ShowAsync();
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            _dialogMutex.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmForgetPeerAsync(string deviceName, string details)
    {
        await _dialogMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ShowOnUiThreadAsync(async () =>
            {
                if (_windowContext.XamlRoot is null)
                {
                    return false;
                }

                var dialog = new ContentDialog
                {
                    Title = $"'{deviceName}' has a different identity",
                    Content = details +
                              "\n\nOnly forget the saved identity if you know why it changed — for example, " +
                              "the app was reinstalled on that device.",
                    PrimaryButtonText = "Forget and trust the new identity",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = _windowContext.XamlRoot,
                };

                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }).ConfigureAwait(false);
        }
        finally
        {
            _dialogMutex.Release();
        }
    }

    /// <summary>Runs an async UI operation on the dispatcher and awaits its result.</summary>
    private Task<bool> ShowOnUiThreadAsync(Func<Task<bool>> operation)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (Exception)
            {
                // A dialog that fails to show must not hang the transfer session
                // waiting for a decision that will never come.
                completion.TrySetResult(false);
            }
        });

        return completion.Task;
    }
}
