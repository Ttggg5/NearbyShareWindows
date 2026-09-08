using NearbyShare.Core.Protocol;

namespace NearbyShare.Core.Networking;

/// <summary>States of one transfer, following the PROTOCOL.md §5 MVP flow.</summary>
public enum TransferState
{
    /// <summary>Nothing has happened yet.</summary>
    Idle,

    /// <summary>TLS handshake done, <c>HELLO</c> not yet exchanged.</summary>
    Connected,

    /// <summary><c>HELLO</c> exchanged and the peer's identity verified (§2 step 5).</summary>
    HelloExchanged,

    /// <summary><c>OFFER</c> sent (sender) or received (receiver); awaiting the accept/reject decision.</summary>
    Offered,

    /// <summary><c>ACCEPT</c> exchanged; file bytes may flow.</summary>
    Accepted,

    /// <summary>File bytes are moving.</summary>
    Transferring,

    /// <summary>The receiver declined the offer.</summary>
    Rejected,

    /// <summary>All files transferred and <c>DONE</c> exchanged.</summary>
    Completed,

    /// <summary>Aborted by a <c>CANCEL</c> from either side.</summary>
    Cancelled,

    /// <summary>Aborted by an error; see <see cref="TransferSession.FailureReason"/>.</summary>
    Failed,
}

/// <summary>Progress of a transfer, suitable for driving a progress bar.</summary>
/// <param name="TransferId">The transfer's UUID.</param>
/// <param name="FileIndex">Zero-based index of the file currently moving.</param>
/// <param name="FileName">Name of the file currently moving.</param>
/// <param name="FileBytesTransferred">Bytes of the current file transferred so far.</param>
/// <param name="FileTotalBytes">Declared size of the current file.</param>
/// <param name="TotalBytesTransferred">Bytes transferred across all files so far.</param>
/// <param name="TotalBytes">Sum of all declared file sizes in the offer.</param>
/// <param name="FileCount">Number of files in the offer.</param>
public readonly record struct TransferProgress(
    string TransferId,
    int FileIndex,
    string FileName,
    long FileBytesTransferred,
    long FileTotalBytes,
    long TotalBytesTransferred,
    long TotalBytes,
    int FileCount)
{
    /// <summary>Overall completion in the range 0.0-1.0.</summary>
    public double OverallFraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)TotalBytesTransferred / TotalBytes, 0, 1);

    /// <summary>Current file completion in the range 0.0-1.0.</summary>
    public double FileFraction => FileTotalBytes <= 0 ? 0 : Math.Clamp((double)FileBytesTransferred / FileTotalBytes, 0, 1);
}

/// <summary>A local file queued for sending.</summary>
/// <param name="Path">Full path on disk.</param>
/// <param name="Name">File name to advertise in the <c>OFFER</c>.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Mime">MIME type, or null.</param>
/// <param name="Sha256">
/// Optional lowercase hex SHA-256. When supplied the receiver verifies it
/// (PROTOCOL.md §5); when null the <c>sha256</c> field is omitted from the offer.
/// </param>
public sealed record OutgoingFile(string Path, string Name, long Size, string? Mime, string? Sha256)
{
    /// <summary>Builds an entry from a path on disk, without computing a checksum.</summary>
    public static OutgoingFile FromPath(string path, string? mime = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File to send does not exist.", path);
        }

        return new OutgoingFile(info.FullName, info.Name, info.Length, mime ?? MimeTypes.Guess(info.Name), Sha256: null);
    }

    /// <summary>The <c>OFFER</c> file entry for this file.</summary>
    public FileEntry ToFileEntry() => new()
    {
        Name = Name,
        Size = Size,
        Mime = Mime,
        Sha256 = Sha256,
    };
}

/// <summary>An inbound offer awaiting the user's accept/reject decision.</summary>
/// <param name="TransferId">The transfer's UUID, assigned by the sender.</param>
/// <param name="PeerDeviceId">The sender's device id, from its <c>HELLO</c>.</param>
/// <param name="PeerDeviceName">The sender's display name, from its <c>HELLO</c>.</param>
/// <param name="Files">The files being offered.</param>
public sealed record IncomingTransferRequest(
    string TransferId,
    string PeerDeviceId,
    string PeerDeviceName,
    IReadOnlyList<FileEntry> Files)
{
    /// <summary>Sum of all declared file sizes.</summary>
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>
/// The receiver-side policy hook: whether to accept an offer, and where each
/// accepted file is written. Implemented in the app by the incoming-transfer
/// dialog; implemented in tests by an auto-accepting stub.
/// </summary>
public interface IIncomingTransferHandler
{
    /// <summary>Asks the user whether to accept. Returning false sends <c>REJECT</c>.</summary>
    Task<bool> ShouldAcceptAsync(IncomingTransferRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the destination stream for one accepted file. The session writes
    /// exactly <c>file.Size</c> bytes and then disposes the stream.
    /// </summary>
    Task<Stream> OpenDestinationAsync(IncomingTransferRequest request, FileEntry file, int fileIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Called after a file has been fully written and its checksum verified, so
    /// the app can move it out of a temporary location and surface it to the user.
    /// </summary>
    Task OnFileCompletedAsync(IncomingTransferRequest request, FileEntry file, int fileIndex, CancellationToken cancellationToken);

    /// <summary>Called when a transfer ends, successfully or not, so partial output can be discarded.</summary>
    Task OnTransferFinishedAsync(IncomingTransferRequest request, TransferState finalState, Exception? error, CancellationToken cancellationToken);
}

/// <summary>Minimal extension-to-MIME mapping for populating the <c>OFFER</c>'s optional <c>mime</c> field.</summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".mov"] = "video/quicktime",
        [".apk"] = "application/vnd.android.package-archive",
        [".exe"] = "application/vnd.microsoft.portable-executable",
    };

    /// <summary>Guesses a MIME type from a file name, defaulting to <c>application/octet-stream</c>.</summary>
    public static string Guess(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return ByExtension.TryGetValue(extension, out string? mime) ? mime : "application/octet-stream";
    }
}
