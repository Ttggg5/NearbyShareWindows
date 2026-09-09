using Microsoft.UI.Xaml.Controls;
using NearbyShare.App.ViewModels;
using NearbyShare.Core.Networking;

namespace NearbyShare.App.Views;

/// <summary>
/// The accept/reject prompt for an inbound <c>OFFER</c> (PROTOCOL.md §5). Shown
/// by <see cref="Services.DialogService"/>, which reads
/// <c>ContentDialogResult.Primary</c> as acceptance.
/// </summary>
public sealed partial class IncomingTransferDialog : ContentDialog
{
    public IncomingTransferViewModel ViewModel { get; }

    public IncomingTransferDialog(IncomingTransferRequest request)
    {
        ViewModel = new IncomingTransferViewModel(request);
        InitializeComponent();
    }
}
