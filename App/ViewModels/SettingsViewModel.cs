using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NearbyShare.App.Services;
using NearbyShare.Core.Persistence;
using NearbyShare.Core.Services;

namespace NearbyShare.App.ViewModels;

/// <summary>
/// Backs <c>SettingsPage</c>: the user-visible device name (re-advertised over
/// mDNS on save, per PROTOCOL.md §1) and the folder received files are saved to.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly INearbyShareService _shareService;
    private readonly IDeviceSettingsRepository _settings;
    private readonly IFilePickerService _filePicker;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDeviceNameCommand))]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _downloadFolder = "(default: Downloads\\NearbyShare)";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDeviceNameCommand))]
    private bool _isBusy;

    public SettingsViewModel(INearbyShareService shareService, IDeviceSettingsRepository settings, IFilePickerService filePicker)
    {
        _shareService = shareService;
        _settings = settings;
        _filePicker = filePicker;
    }

    /// <summary>Called when the page appears: loads the current settings.</summary>
    public async Task OnNavigatedToAsync()
    {
        // The listener may already be running (it starts at app launch), in which
        // case its Identity is the freshest source; otherwise fall back to reading
        // the setting directly so the field is never blank before StartAsync completes.
        DeviceName = _shareService.IsRunning
            ? _shareService.Identity.DeviceName
            : await _settings.GetOrCreateAsync(SettingKeys.DeviceName, () => Environment.MachineName).ConfigureAwait(false);

        string? folder = await _settings.GetAsync(SettingKeys.DownloadFolder).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            DownloadFolder = folder;
        }
    }

    private bool CanSaveDeviceName() => !IsBusy && !string.IsNullOrWhiteSpace(DeviceName);

    [RelayCommand(CanExecute = nameof(CanSaveDeviceName))]
    private async Task SaveDeviceNameAsync()
    {
        IsBusy = true;
        try
        {
            await _shareService.SetDeviceNameAsync(DeviceName.Trim()).ConfigureAwait(false);
            StatusMessage = "Device name saved and re-announced on the network.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChooseDownloadFolderAsync()
    {
        string? folder = await _filePicker.PickDownloadFolderAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        await _settings.SetAsync(SettingKeys.DownloadFolder, folder).ConfigureAwait(false);
        DownloadFolder = folder;

        // NearbyShareOptions.DownloadFolder is captured once at app launch
        // (App.xaml.cs ConfigureServices), so this setting only takes effect on
        // the next run until Core exposes a way to change it live.
        StatusMessage = "Download folder saved. Restart NearbyShare for the change to take effect.";
    }
}
