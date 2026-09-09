using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using NearbyShare.App.ViewModels;

namespace NearbyShare.App.Views;

/// <summary>Progress bar and status for whichever transfer is currently active, in either direction.</summary>
public sealed partial class ProgressPage : Page
{
    public ProgressViewModel ViewModel { get; }

    public ProgressPage()
    {
        // Singleton: the same instance TransferListenerService and
        // DeviceListViewModel already push updates into.
        ViewModel = App.Current.Services.GetRequiredService<ProgressViewModel>();
        InitializeComponent();
    }
}
