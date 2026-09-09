using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using NearbyShare.App.Services;
using NearbyShare.App.ViewModels;
using NearbyShare.Core.Discovery;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Persistence;
using NearbyShare.Core.Protocol;
using NearbyShare.Core.Services;

namespace NearbyShare.App;

/// <summary>
/// Application entry point. Composes the dependency-injection container and
/// starts the background transfer listener that runs for the app's lifetime.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    /// <summary>The composition root. Views resolve their ViewModels from here.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The running application, typed.</summary>
    public static new App Current => (App)Application.Current;

    /// <summary>The main window, needed for window-handle interop (pickers, dialogs).</summary>
    public Window? MainWindow => _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();

        // The UI dispatcher and window handle can only be captured once a window
        // exists, so services that need them are initialized here rather than in
        // the container.
        Services.GetRequiredService<IUiDispatcher>().Attach(_window.DispatcherQueue);
        Services.GetRequiredService<IWindowContext>().Attach(_window);

        _window.Activate();

        // PROTOCOL.md §1: the device advertises and listens for as long as the app
        // is running, not just while a transfer page is open.
        _ = Services.GetRequiredService<TransferListenerService>().StartAsync();

        _window.Closed += async (_, _) =>
        {
            await Services.GetRequiredService<TransferListenerService>().StopAsync();
        };
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        AppPaths paths = AppPaths.CreateDefault();

        services.AddSingleton(paths);
        services.AddSingleton(new NearbyShareOptions
        {
            DataFolder = paths.DataFolder,
            DownloadFolder = paths.DownloadFolder,
            ListenPort = 0, // Let the OS pick; the bound port is advertised over mDNS.
            Os = ProtocolConstants.OsWindows,
        });

        // ---------------------------------------------------------------
        // Core: every registration is interface -> implementation, so a
        // ViewModel can only ever depend on the abstraction.
        // ---------------------------------------------------------------
        services.AddSingleton<IDeviceSettingsRepository>(
            _ => new JsonDeviceSettingsRepository(paths.SettingsPath));

        // Interface-only seam for now (PROTOCOL-independent): swapping in a real
        // store later is this one line.
        services.AddSingleton<ITransferHistoryRepository, NoOpTransferHistoryRepository>();

        services.AddSingleton<IKnownPeerStore>(_ => new JsonKnownPeerStore(paths.KnownPeersPath));
        services.AddSingleton<IDeviceIdentityProvider, DeviceIdentityProvider>();
        services.AddSingleton<IDiscoveryService, MdnsDiscoveryService>();
        services.AddSingleton<INearbyShareService, NearbyShareService>();

        // ---------------------------------------------------------------
        // App services
        // ---------------------------------------------------------------
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<IWindowContext, WindowContext>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<TransferListenerService>();

        // ---------------------------------------------------------------
        // ViewModels
        // ---------------------------------------------------------------
        services.AddSingleton<DeviceListViewModel>();
        services.AddSingleton<ProgressViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // ---------------------------------------------------------------
        // Views
        // ---------------------------------------------------------------
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
