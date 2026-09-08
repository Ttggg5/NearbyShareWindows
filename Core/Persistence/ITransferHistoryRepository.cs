namespace NearbyShare.Core.Persistence;

/// <summary>Which way a transfer went.</summary>
public enum TransferDirection
{
    /// <summary>Files this device sent.</summary>
    Outgoing,

    /// <summary>Files this device received.</summary>
    Incoming,
}

/// <summary>How a transfer ended.</summary>
public enum TransferOutcome
{
    Completed,
    Rejected,
    Cancelled,
    Failed,
}

/// <summary>One completed (or failed) transfer, as it would appear in a history list.</summary>
/// <param name="TransferId">The transfer's UUID.</param>
/// <param name="Direction">Whether the files were sent or received.</param>
/// <param name="PeerDeviceId">The other device's stable UUID.</param>
/// <param name="PeerDeviceName">The other device's display name at the time.</param>
/// <param name="FileNames">Names of the files involved.</param>
/// <param name="TotalBytes">Total declared size.</param>
/// <param name="BytesTransferred">Bytes actually moved before the transfer ended.</param>
/// <param name="StartedAt">When the transfer started.</param>
/// <param name="FinishedAt">When it ended.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ErrorCode">The <c>ERROR</c> code, when it failed.</param>
public sealed record TransferHistoryEntry(
    string TransferId,
    TransferDirection Direction,
    string PeerDeviceId,
    string PeerDeviceName,
    IReadOnlyList<string> FileNames,
    long TotalBytes,
    long BytesTransferred,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TransferOutcome Outcome,
    string? ErrorCode = null);

/// <summary>
/// Persistence seam for transfer history.
/// </summary>
/// <remarks>
/// Intentionally interface-only for the MVP: the app records history through this
/// interface from day one and <see cref="NoOpTransferHistoryRepository"/> is the
/// registered implementation, so adding a real store later is a single DI
/// registration change with no ViewModel edits.
/// </remarks>
public interface ITransferHistoryRepository
{
    /// <summary>Records a finished transfer.</summary>
    Task AddAsync(TransferHistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent transfers, newest first.</summary>
    Task<IReadOnlyList<TransferHistoryEntry>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Deletes all recorded history.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The MVP implementation: accepts and discards. Keeps the call sites in place so
/// history becomes a matter of swapping the DI registration.
/// </summary>
public sealed class NoOpTransferHistoryRepository : ITransferHistoryRepository
{
    /// <inheritdoc />
    public Task AddAsync(TransferHistoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<TransferHistoryEntry>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TransferHistoryEntry>>(Array.Empty<TransferHistoryEntry>());

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
