using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NearbyShare.App.Services;

/// <summary>
/// Marshals work onto the UI thread. Core raises its events on background
/// threads, so every ViewModel update that touches a bound collection has to come
/// back through here.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Binds the dispatcher to the main window's queue. Called once at launch.</summary>
    void Attach(DispatcherQueue queue);

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    void Post(Action action);
}

/// <inheritdoc />
public sealed class UiDispatcher : IUiDispatcher
{
    private DispatcherQueue? _queue;

    public void Attach(DispatcherQueue queue) => _queue = queue;

    public void Post(Action action)
    {
        DispatcherQueue? queue = _queue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}

/// <summary>
/// Supplies the main window and its native handle. WinUI 3 desktop pickers and
/// dialogs both need one, and neither exists until <c>OnLaunched</c>.
/// </summary>
public interface IWindowContext
{
    void Attach(Window window);

    Window? Window { get; }

    /// <summary>The HWND, required by <c>InitializeWithWindow</c> for pickers.</summary>
    nint WindowHandle { get; }

    /// <summary>The <c>XamlRoot</c> every <c>ContentDialog</c> must be given in WinUI 3.</summary>
    XamlRoot? XamlRoot { get; }
}

/// <inheritdoc />
public sealed class WindowContext : IWindowContext
{
    public Window? Window { get; private set; }

    public nint WindowHandle => Window is null ? nint.Zero : WindowNative.GetWindowHandle(Window);

    public XamlRoot? XamlRoot => Window?.Content?.XamlRoot;

    public void Attach(Window window) => Window = window;
}

/// <summary>Wraps the WinUI file pickers so ViewModels stay free of UI types.</summary>
public interface IFilePickerService
{
    /// <summary>Shows a multi-select file picker. Returns the chosen paths, possibly empty.</summary>
    Task<IReadOnlyList<string>> PickFilesToSendAsync();

    /// <summary>Shows a folder picker, for choosing where received files are saved.</summary>
    Task<string?> PickDownloadFolderAsync();
}

/// <inheritdoc />
public sealed class FilePickerService : IFilePickerService
{
    private readonly IWindowContext _windowContext;

    public FilePickerService(IWindowContext windowContext)
    {
        _windowContext = windowContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PickFilesToSendAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };

        // Required for every picker in a WinUI 3 desktop app: without an owner
        // window handle the picker throws instead of appearing.
        InitializeWithWindow.Initialize(picker, _windowContext.WindowHandle);

        // A picker with no file-type filter never opens.
        picker.FileTypeFilter.Add("*");

        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> PickDownloadFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        InitializeWithWindow.Initialize(picker, _windowContext.WindowHandle);
        picker.FileTypeFilter.Add("*");

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
