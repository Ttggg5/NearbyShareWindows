using NearbyShare.Core.Networking;
using NearbyShare.Core.Protocol;

namespace NearbyShare.Core.Services;

/// <summary>
/// Writes accepted files into a folder, delegating the accept/reject decision to
/// a callback. The WinUI app supplies a callback that shows the incoming-transfer
/// dialog; tests supply one that auto-accepts.
/// </summary>
/// <remarks>
/// Each file is written to a <c>.part</c> temporary file and renamed only after
/// the whole file has arrived and any declared checksum has verified, so a failed
/// or cancelled transfer never leaves a plausible-looking truncated file in the
/// user's downloads folder.
/// </remarks>
public sealed class FolderIncomingTransferHandler : IIncomingTransferHandler
{
    private readonly string _destinationFolder;
    private readonly Func<IncomingTransferRequest, CancellationToken, Task<bool>> _acceptCallback;
    private readonly Action<string>? _onFileSaved;
    private readonly Dictionary<string, string> _partialPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _finalPaths = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="destinationFolder">Folder to write received files into. Created if missing.</param>
    /// <param name="acceptCallback">Asks the user; returning false sends <c>REJECT</c>.</param>
    /// <param name="onFileSaved">Called with the final path of each saved file.</param>
    public FolderIncomingTransferHandler(
        string destinationFolder,
        Func<IncomingTransferRequest, CancellationToken, Task<bool>> acceptCallback,
        Action<string>? onFileSaved = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        _destinationFolder = destinationFolder;
        _acceptCallback = acceptCallback ?? throw new ArgumentNullException(nameof(acceptCallback));
        _onFileSaved = onFileSaved;
    }

    /// <summary>Final paths of every file saved by this handler, in arrival order.</summary>
    public IReadOnlyList<string> SavedFilePaths
    {
        get
        {
            lock (_gate)
            {
                return _finalPaths.Values.ToList();
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> ShouldAcceptAsync(IncomingTransferRequest request, CancellationToken cancellationToken) =>
        _acceptCallback(request, cancellationToken);

    /// <inheritdoc />
    public Task<Stream> OpenDestinationAsync(
        IncomingTransferRequest request,
        FileEntry file,
        int fileIndex,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_destinationFolder);

        // The name comes from an untrusted peer; never let it escape the folder.
        string safeName = FileNameSanitizer.Sanitize(file.Name);
        string finalPath = FileNameSanitizer.ResolveUniquePath(_destinationFolder, safeName);
        string partialPath = finalPath + ".part";

        lock (_gate)
        {
            _partialPaths[Key(request.TransferId, fileIndex)] = partialPath;
            _finalPaths[Key(request.TransferId, fileIndex)] = finalPath;
        }

        Stream stream = new FileStream(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            TransferSession.StreamBufferSize,
            useAsync: true);

        return Task.FromResult(stream);
    }

    /// <inheritdoc />
    public Task OnFileCompletedAsync(
        IncomingTransferRequest request,
        FileEntry file,
        int fileIndex,
        CancellationToken cancellationToken)
    {
        string key = Key(request.TransferId, fileIndex);
        string partialPath;
        string finalPath;

        lock (_gate)
        {
            if (!_partialPaths.TryGetValue(key, out partialPath!) || !_finalPaths.TryGetValue(key, out finalPath!))
            {
                return Task.CompletedTask;
            }

            _partialPaths.Remove(key);
        }

        // Another file may have claimed the name while this one was in flight.
        if (File.Exists(finalPath))
        {
            finalPath = FileNameSanitizer.ResolveUniquePath(_destinationFolder, Path.GetFileName(finalPath));
            lock (_gate)
            {
                _finalPaths[key] = finalPath;
            }
        }

        File.Move(partialPath, finalPath, overwrite: false);
        _onFileSaved?.Invoke(finalPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnTransferFinishedAsync(
        IncomingTransferRequest request,
        TransferState finalState,
        Exception? error,
        CancellationToken cancellationToken)
    {
        if (finalState == TransferState.Completed)
        {
            return Task.CompletedTask;
        }

        // PROTOCOL.md §5 step 6: on cancel, discard partial output.
        List<string> leftovers;
        lock (_gate)
        {
            leftovers = _partialPaths
                .Where(pair => pair.Key.StartsWith(request.TransferId + "/", StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .ToList();

            foreach (string key in _partialPaths.Keys
                         .Where(k => k.StartsWith(request.TransferId + "/", StringComparison.Ordinal))
                         .ToList())
            {
                _partialPaths.Remove(key);
            }
        }

        foreach (string path in leftovers)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A locked partial file is not worth failing the cleanup over.
            }
        }

        return Task.CompletedTask;
    }

    private static string Key(string transferId, int fileIndex) => $"{transferId}/{fileIndex}";
}
