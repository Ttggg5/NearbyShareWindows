using NearbyShare.App.Services;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Protocol;

namespace NearbyShare.App.ViewModels;

/// <summary>
/// Backs <c>IncomingTransferDialog</c>: formats an inbound <c>OFFER</c>
/// (PROTOCOL.md §5) for the user's accept/reject decision. The decision itself is
/// read from the dialog's button result by <see cref="DialogService"/> — this
/// ViewModel only presents what is being offered.
/// </summary>
public sealed class IncomingTransferViewModel
{
    public IncomingTransferViewModel(IncomingTransferRequest request)
    {
        Request = request;
        Files = request.Files.Select(f => new IncomingFileRowViewModel(f)).ToList();
    }

    /// <summary>The offer this dialog is presenting.</summary>
    public IncomingTransferRequest Request { get; }

    public string PeerDeviceName => Request.PeerDeviceName;

    public string FileCountSummary => Request.Files.Count == 1 ? "1 file" : $"{Request.Files.Count} files";

    public string TotalSizeSummary => ByteFormatter.Format(Request.TotalBytes);

    /// <summary>One sentence describing the offer, for the dialog's headline text.</summary>
    public string OfferSummary => $"{PeerDeviceName} wants to send you {FileCountSummary} ({TotalSizeSummary}).";

    /// <summary>The offered files, for the dialog's file list.</summary>
    public IReadOnlyList<IncomingFileRowViewModel> Files { get; }
}

/// <summary>One row of the offered-files list in <c>IncomingTransferDialog</c>.</summary>
public sealed class IncomingFileRowViewModel
{
    public IncomingFileRowViewModel(FileEntry file)
    {
        Name = file.Name;
        SizeSummary = ByteFormatter.Format(file.Size);
    }

    public string Name { get; }

    public string SizeSummary { get; }
}
