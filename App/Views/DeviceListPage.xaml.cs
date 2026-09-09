using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NearbyShare.App.ViewModels;

namespace NearbyShare.App.Views;

/// <summary>The peer list, the send-files entry point, and the manual-IP fallback.</summary>
public sealed partial class DeviceListPage : Page
{
    public DeviceListViewModel ViewModel { get; }

    public DeviceListPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<DeviceListViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo();
    }
}
