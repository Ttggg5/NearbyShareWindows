using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NearbyShare.App.Views;

namespace NearbyShare.App;

/// <summary>
/// The app's single top-level window: a <see cref="NavigationView"/> switching a
/// <see cref="Frame"/> between the Devices, Transfer and Settings pages.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "NearbyShare";

        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(DeviceListPage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        Type pageType = tag switch
        {
            "progress" => typeof(ProgressPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DeviceListPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
