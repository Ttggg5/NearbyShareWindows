using System.Security.Cryptography;
using NearbyShare.Core.Protocol;

namespace NearbyShare.Core.Networking;

/// <summary>
/// Identifies this device during the <c>HELLO</c> exchange (PROTOCOL.md §5).
/// </summary>
/// <param name="DeviceId">This device's stable UUID.</param>
/// <param name="DeviceName">This device's display name.</param>
public readonly record struct LocalIdentity(string DeviceId, string DeviceName);

/// <summary>
/// Drives one transfer over one established TLS connection, implementing the
/// PROTOCOL.md §5 MVP flow as a state machine for either role.
/// </summary>
/// <remarks>
/// The sender and receiver halves are deliberately in one class: they share the
/// <c>HELLO</c> exchange, the identity verification of §2 step 5, the framing
/// rules, and the error reporting of §5 step 7.
/// </remarks>
public sealed class TransferSession
{
    /// <summary>Buffer size for the raw file byte stream.</summary>
    public const int StreamBufferSize = 64 * 1024;

    /// <summary>Minimum interval between emitted <c>PROGRESS</c> messages.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stream _stream;
    private readonly LocalIdentity _identity;
    private readonly TofuCertificateValidator? _validator;
    private readonly string? _peerCertificateFingerprint;

    /// <param name="stream">The authenticated TLS stream from a <see cref="TlsConnection"/>.</param>
    /// <param name="identity">This device's identity, sent in <c>HELLO</c>.</param>
    /// <param name="validator">
    /// Used to run the TOFU check once the peer's id is known from <c>HELLO</c>.
    /// Required for inbound connections; optional for outbound ones, where the
    /// check already ran during the handshake.
    /// </param>
    /// <param name="peerCertificateFingerprint">
    /// The fingerprint recorded during the handshake, needed for the post-<c>HELLO</c>
    /// check on inbound connections.
    /// </param>
    public TransferSession(
        Stream stream,
        LocalIdentity identity,
        TofuCertificateValidator? validator = null,
        string? peerCertificateFingerprint = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _identity = identity;
        _validator = validator;
        _peerCertificateFingerprint = peerCertificateFingerprint;
    }

    /// <summary>The current state of the flow.</summary>
    public TransferState State { get; private set; } = TransferState.Idle;

    /// <summary>The transfer's UUID once an <c>OFFER</c> has been sent or received.</summary>
    public string? TransferId { get; private set; }

    /// <summary>The peer's device id, from its <c>HELLO</c>.</summary>
    public string? PeerDeviceId { get; private set; }

    /// <summary>The peer's display name, from its <c>HELLO</c>.</summary>
    public string? PeerDeviceName { get; private set; }

    /// <summary>Why the transfer failed, if it did.</summary>
    public Exception? FailureReason { get; private set; }

    /// <summary>Raised on every state transition.</summary>
    public event EventHandler<TransferState>? StateChanged;

    /// <summary>Raised for every progress update, in addition to the <c>IProgress</c> callback.</summary>
    public event EventHandler<TransferProgress>? ProgressChanged;

    // =====================================================================
    // Sender role
    // =====================================================================

    /// <summary>
    /// Runs the sender half of PROTOCOL.md §5: <c>HELLO</c>, <c>OFFER</c>, wait for
    /// <c>ACCEPT</c>/<c>REJECT</c>, stream every file's raw bytes back-to-back, then
    /// <c>DONE{fileIndex:"all"}</c>.
    /// </summary>
    /// <param name="files">The files to offer, in the order they will be streamed.</param>
    /// <param name="expectedPeerDeviceId">
    /// The device id we believe we dialled. When set, a mismatch in the peer's
    /// <c>HELLO</c> aborts with <c>ERROR{IDENTITY_MISMATCH}</c> per §2 step 5.
    /// </param>
    /// <param name="progress">Receives progress updates.</param>
    /// <returns>True if the receiver accepted and all files were sent.</returns>
    public async Task<bool> SendAsync(
        IReadOnlyList<OutgoingFile> files,
        string? expectedPeerDeviceId = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file is required.", nameof(files));
        }

        string transferId = Guid.NewGuid().ToString();
        TransferId = transferId;
        SetState(TransferState.Connected);

        try
        {
            // §2 step 5: the connecting side sends HELLO first.
            await MessageCodec.WriteAsync(_stream, HelloMessage.Create(_identity.DeviceId, _identity.DeviceName), cancellationToken)
                .ConfigureAwait(false);

            HelloPayload peerHello = await ReceiveHelloAsync(expectedPeerDeviceId, cancellationToken).ConfigureAwait(false);
            PeerDeviceId = peerHello.DeviceId;
            PeerDeviceName = peerHello.DeviceName;
            SetState(TransferState.HelloExchanged);

            // §5 step 2.
            var offer = OfferMessage.Create(transferId, files.Select(f => f.ToFileEntry()));
            await MessageCodec.WriteAsync(_stream, offer, cancellationToken).ConfigureAwait(false);
            SetState(TransferState.Offered);

            // §5 step 3.
            Message response = await ReadDispatchableRequiredAsync(cancellationToken).ConfigureAwait(false);
            switch (response)
            {
                case AcceptMessage accept when accept.Payload.TransferId == transferId:
                    SetState(TransferState.Accepted);
                    break;

                case RejectMessage reject:
                    SetState(TransferState.Rejected);
                    FailureReason = new TransferRejectedException(reject.Payload.Reason);
                    return false;

                case CancelMessage:
                    SetState(TransferState.Cancelled);
                    return false;

                case ErrorMessage error:
                    throw new PeerReportedErrorException(error.Payload.Code, error.Payload.Message);

                default:
                    throw new MalformedMessageException(
                        $"Expected ACCEPT or REJECT for transfer {transferId} but received {response.Type}.");
            }

            // §5 step 4: raw bytes, in offer order, back-to-back, no extra framing.
            SetState(TransferState.Transferring);
            long totalBytes = files.Sum(f => f.Size);
            long overallSent = 0;

            for (int index = 0; index < files.Count; index++)
            {
                OutgoingFile file = files[index];
                cancellationToken.ThrowIfCancellationRequested();

                await using FileStream source = new(
                    file.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    StreamBufferSize,
                    useAsync: true);

                // Nothing may be written between files: PROTOCOL.md §5 step 4
                // streams them "back-to-back, with no additional framing", and the
                // receiver switches straight from the last byte of one file to the
                // first byte of the next. A control frame injected here would be
                // consumed as file content and desynchronize the whole stream.
                overallSent = await PumpAsync(
                    source,
                    _stream,
                    file.Size,
                    transferId,
                    index,
                    file.Name,
                    files.Count,
                    overallSent,
                    totalBytes,
                    hasher: null,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            // §5 step 5.
            await MessageCodec.WriteAsync(_stream, DoneMessage.CreateAll(transferId), cancellationToken).ConfigureAwait(false);
            SetState(TransferState.Completed);
            return true;
        }
        catch (OperationCanceledException)
        {
            await TrySendCancelAsync(transferId).ConfigureAwait(false);
            SetState(TransferState.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            FailureReason = ex;
            await TrySendErrorAsync(transferId, ex).ConfigureAwait(false);
            SetState(TransferState.Failed);
            throw;
        }
    }

    // =====================================================================
    // Receiver role
    // =====================================================================

    /// <summary>
    /// Runs the receiver half of PROTOCOL.md §5: read <c>HELLO</c>, reply
    /// <c>HELLO</c>, read <c>OFFER</c>, ask <paramref name="handler"/> whether to
    /// accept, then read each file's raw bytes and verify any supplied checksum.
    /// </summary>
    /// <param name="handler">Decides acceptance and supplies destination streams.</param>
    /// <param name="progress">Receives progress updates.</param>
    /// <returns>True if an offer was accepted and fully received.</returns>
    public async Task<bool> ReceiveAsync(
        IIncomingTransferHandler handler,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        SetState(TransferState.Connected);
        IncomingTransferRequest? request = null;

        try
        {
            // §2 step 5: the connecting side speaks first.
            HelloPayload peerHello = await ReceiveHelloAsync(expectedPeerDeviceId: null, cancellationToken).ConfigureAwait(false);
            PeerDeviceId = peerHello.DeviceId;
            PeerDeviceName = peerHello.DeviceName;

            // §2 step 4/5: now that the id is known, pin (or verify) the
            // certificate recorded during the handshake.
            VerifyInboundPeerIdentity(peerHello.DeviceId, peerHello.DeviceName);

            await MessageCodec.WriteAsync(_stream, HelloMessage.Create(_identity.DeviceId, _identity.DeviceName), cancellationToken)
                .ConfigureAwait(false);
            SetState(TransferState.HelloExchanged);

            Message message = await ReadDispatchableRequiredAsync(cancellationToken).ConfigureAwait(false);
            if (message is not OfferMessage offer)
            {
                if (message is CancelMessage)
                {
                    SetState(TransferState.Cancelled);
                    return false;
                }

                throw new MalformedMessageException($"Expected OFFER after HELLO but received {message.Type}.");
            }

            string transferId = offer.Payload.TransferId;
            TransferId = transferId;
            SetState(TransferState.Offered);

            request = new IncomingTransferRequest(
                transferId,
                peerHello.DeviceId,
                peerHello.DeviceName,
                offer.Payload.Files);

            ValidateOffer(offer.Payload);

            // §5 step 3.
            bool accepted = await handler.ShouldAcceptAsync(request, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                await MessageCodec.WriteAsync(_stream, RejectMessage.Create(transferId, "Declined by the user."), cancellationToken)
                    .ConfigureAwait(false);
                SetState(TransferState.Rejected);
                await handler.OnTransferFinishedAsync(request, TransferState.Rejected, null, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await MessageCodec.WriteAsync(_stream, AcceptMessage.Create(transferId), cancellationToken).ConfigureAwait(false);
            SetState(TransferState.Accepted);
            SetState(TransferState.Transferring);

            long totalBytes = offer.Payload.TotalBytes;
            long overallReceived = 0;

            for (int index = 0; index < offer.Payload.Files.Count; index++)
            {
                FileEntry file = offer.Payload.Files[index];
                cancellationToken.ThrowIfCancellationRequested();

                using var hasher = string.IsNullOrWhiteSpace(file.Sha256) ? null : SHA256.Create();

                await using (Stream destination = await handler
                                 .OpenDestinationAsync(request, file, index, cancellationToken).ConfigureAwait(false))
                {
                    overallReceived = await PumpAsync(
                        _stream,
                        destination,
                        file.Size,
                        transferId,
                        index,
                        file.Name,
                        offer.Payload.Files.Count,
                        overallReceived,
                        totalBytes,
                        hasher,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (hasher is not null)
                {
                    hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    string actual = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
                    string expected = file.Sha256!.Trim().ToLowerInvariant();
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    {
                        // §5 step 7: a checksum mismatch is reported via ERROR.
                        throw new ChecksumMismatchException(file.Name, expected, actual);
                    }
                }

                await handler.OnFileCompletedAsync(request, file, index, cancellationToken).ConfigureAwait(false);
            }

            // §5 step 5: the sender's per-file and final DONE messages arrive after
            // the bytes. Tolerate a sender that only sends the final one.
            await DrainToFinalDoneAsync(transferId, cancellationToken).ConfigureAwait(false);

            SetState(TransferState.Completed);
            await handler.OnTransferFinishedAsync(request, TransferState.Completed, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            await TrySendCancelAsync(TransferId).ConfigureAwait(false);
            SetState(TransferState.Cancelled);
            await NotifyFinishedAsync(handler, request, TransferState.Cancelled, ex).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            FailureReason = ex;
            await TrySendErrorAsync(TransferId, ex).ConfigureAwait(false);
            SetState(TransferState.Failed);
            await NotifyFinishedAsync(handler, request, TransferState.Failed, ex).ConfigureAwait(false);
            throw;
        }
    }

    // =====================================================================
    // Shared plumbing
    // =====================================================================

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes, reporting progress locally.
    /// </summary>
    /// <remarks>
    /// No <c>PROGRESS</c> message is written to the socket from here. PROTOCOL.md
    /// §5 step 4 makes them optional ("may emit"), and while file bytes are in
    /// flight the connection is carrying an unframed raw stream — a control frame
    /// written into it would be read back as file content. Progress therefore
    /// drives the UI through <see cref="IProgress{T}"/> and
    /// <see cref="ProgressChanged"/> only. Inbound <c>PROGRESS</c> messages are
    /// still accepted wherever the session is reading control messages, so a peer
    /// that does send them costs us nothing.
    /// </remarks>
    private async Task<long> PumpAsync(
        Stream source,
        Stream destination,
        long length,
        string transferId,
        int fileIndex,
        string fileName,
        int fileCount,
        long overallAlready,
        long overallTotal,
        HashAlgorithm? hasher,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[StreamBufferSize];
        long remaining = length;
        long fileTransferred = 0;
        long overall = overallAlready;
        DateTimeOffset lastReport = DateTimeOffset.MinValue;

        void Report(bool force)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!force && now - lastReport < ProgressInterval)
            {
                return;
            }

            lastReport = now;
            var snapshot = new TransferProgress(
                transferId, fileIndex, fileName, fileTransferred, length, overall, overallTotal, fileCount);
            progress?.Report(snapshot);
            ProgressChanged?.Invoke(this, snapshot);
        }

        Report(force: true);

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // The declared size in the OFFER is the contract; a short stream
                // means the peer died or lied.
                throw new IncompleteFrameException((int)Math.Min(int.MaxValue, fileTransferred), (int)Math.Min(int.MaxValue, length));
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hasher?.TransformBlock(buffer, 0, read, null, 0);

            remaining -= read;
            fileTransferred += read;
            overall += read;

            Report(force: false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        Report(force: true);
        return overall;
    }

    /// <summary>Reads the peer's <c>HELLO</c> and applies the checks of PROTOCOL.md §2 step 5 and §6.</summary>
    private async Task<HelloPayload> ReceiveHelloAsync(string? expectedPeerDeviceId, CancellationToken cancellationToken)
    {
        Message message = await ReadDispatchableRequiredAsync(cancellationToken).ConfigureAwait(false);
        if (message is not HelloMessage hello)
        {
            throw new MalformedMessageException($"Expected HELLO as the first message but received {message.Type}.");
        }

        if (hello.Payload.ProtocolVersion != ProtocolConstants.Version)
        {
            throw new UnsupportedProtocolVersionException(hello.Payload.ProtocolVersion);
        }

        if (string.IsNullOrWhiteSpace(hello.Payload.DeviceId))
        {
            throw new MalformedMessageException("HELLO did not carry a deviceId.");
        }

        // §2 step 5: guards against a peer's IP being reused or spoofed on the LAN.
        if (!string.IsNullOrWhiteSpace(expectedPeerDeviceId) &&
            !string.Equals(expectedPeerDeviceId, hello.Payload.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new PeerIdentityMismatchException(expectedPeerDeviceId!, hello.Payload.DeviceId);
        }

        return hello.Payload;
    }

    /// <summary>
    /// Runs the TOFU check for an inbound connection, now that <c>HELLO</c> has
    /// revealed which device we are talking to (PROTOCOL.md §2 steps 4-5).
    /// </summary>
    private void VerifyInboundPeerIdentity(string peerDeviceId, string peerDeviceName)
    {
        if (_validator is null || _peerCertificateFingerprint is null)
        {
            return;
        }

        // The certificate itself was consumed during the handshake; we only kept
        // its fingerprint, so pin/verify from that.
        TofuValidationResult result = _validator.ValidateFingerprint(
            peerDeviceId,
            advertisedFingerprint: null,
            presentedFingerprint: _peerCertificateFingerprint,
            deviceName: peerDeviceName);

        if (!result.IsTrusted)
        {
            throw new PeerIdentityChangedException(
                peerDeviceId,
                result.ExpectedFingerprint ?? string.Empty,
                result.PresentedFingerprint);
        }
    }

    private static async Task NotifyFinishedAsync(
        IIncomingTransferHandler handler,
        IncomingTransferRequest? request,
        TransferState state,
        Exception? error)
    {
        if (request is null)
        {
            return;
        }

        try
        {
            await handler.OnTransferFinishedAsync(request, state, error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup callbacks must not mask the original failure.
        }
    }

    /// <summary>Rejects an offer that is unusable before any bytes move.</summary>
    private static void ValidateOffer(OfferPayload offer)
    {
        if (string.IsNullOrWhiteSpace(offer.TransferId))
        {
            throw new MalformedMessageException("OFFER did not carry a transferId.");
        }

        if (offer.Files.Count == 0)
        {
            throw new MalformedMessageException("OFFER contained no files.");
        }

        foreach (FileEntry file in offer.Files)
        {
            if (file.Size < 0)
            {
                throw new MalformedMessageException($"OFFER declared a negative size for '{file.Name}'.");
            }
        }
    }

    /// <summary>
    /// Consumes the sender's trailing per-file <c>DONE</c> messages until the final
    /// <c>DONE{fileIndex:"all"}</c> arrives.
    /// </summary>
    private async Task DrainToFinalDoneAsync(string transferId, CancellationToken cancellationToken)
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            Message? message = await MessageDispatcher.ReadDispatchableAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                // Sender closed without a final DONE. All declared bytes arrived and
                // any checksums verified, so treat the transfer as complete.
                return;
            }

            switch (message)
            {
                case DoneMessage done when done.Payload.FileIndex.IsAll:
                    return;

                case DoneMessage:
                    continue;

                case ProgressMessage:
                    continue;

                case CancelMessage:
                    throw new TransferCancelledByPeerException(transferId);

                case ErrorMessage error:
                    throw new PeerReportedErrorException(error.Payload.Code, error.Payload.Message);

                default:
                    continue;
            }
        }

        throw new MalformedMessageException("Peer sent too many messages without a final DONE.");
    }

    private async Task<Message> ReadDispatchableRequiredAsync(CancellationToken cancellationToken)
    {
        Message? message = await MessageDispatcher.ReadDispatchableAsync(_stream, cancellationToken).ConfigureAwait(false);
        return message ?? throw new IncompleteFrameException(0, ProtocolConstants.LengthPrefixBytes);
    }

    private async Task TrySendErrorAsync(string? transferId, Exception exception)
    {
        try
        {
            string code = exception switch
            {
                ProtocolException protocolException => protocolException.ErrorCode,
                IOException io when IsDiskFull(io) => ErrorCodes.DiskFull,
                IOException => ErrorCodes.IoError,
                _ => ErrorCodes.InternalError,
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await MessageCodec.WriteAsync(
                _stream,
                ErrorMessage.Create(transferId, code, exception.Message),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection is already failing; a best-effort ERROR is all §5
            // step 7 can ask for.
        }
    }

    private async Task TrySendCancelAsync(string? transferId)
    {
        if (string.IsNullOrEmpty(transferId))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await MessageCodec.WriteAsync(_stream, CancelMessage.Create(transferId), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>Recognizes the Windows and Unix "no space left" errors so §5 step 7 can report DISK_FULL.</summary>
    private static bool IsDiskFull(IOException exception)
    {
        const int ErrorDiskFull = 0x70;
        const int ErrorHandleDiskFull = 0x27;
        int hr = exception.HResult & 0xFFFF;
        return hr is ErrorDiskFull or ErrorHandleDiskFull ||
               exception.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase);
    }

    private void SetState(TransferState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }
}

/// <summary>The receiver declined the offer.</summary>
public sealed class TransferRejectedException : ProtocolException
{
    public TransferRejectedException(string? reason)
        : base(string.IsNullOrWhiteSpace(reason) ? "The peer declined the transfer." : $"The peer declined the transfer: {reason}",
            ErrorCodes.Cancelled)
    {
        Reason = reason;
    }

    public string? Reason { get; }
}

/// <summary>The peer sent an <c>ERROR</c> message.</summary>
public sealed class PeerReportedErrorException : ProtocolException
{
    public PeerReportedErrorException(string code, string? message)
        : base($"The peer reported an error ({code}): {message}", code)
    {
    }
}

/// <summary>The peer sent <c>CANCEL</c>.</summary>
public sealed class TransferCancelledByPeerException : ProtocolException
{
    public TransferCancelledByPeerException(string transferId)
        : base($"The peer cancelled transfer {transferId}.", ErrorCodes.Cancelled)
    {
    }
}

/// <summary>
/// The <c>deviceId</c> in <c>HELLO</c> did not match the peer we believed we had
/// connected to (PROTOCOL.md §2 step 5).
/// </summary>
public sealed class PeerIdentityMismatchException : ProtocolException
{
    public PeerIdentityMismatchException(string expectedDeviceId, string actualDeviceId)
        : base($"Connected to device '{actualDeviceId}' but expected '{expectedDeviceId}'.", ErrorCodes.IdentityMismatch)
    {
        ExpectedDeviceId = expectedDeviceId;
        ActualDeviceId = actualDeviceId;
    }

    public string ExpectedDeviceId { get; }

    public string ActualDeviceId { get; }
}

/// <summary>A received file's SHA-256 did not match the one declared in the <c>OFFER</c>.</summary>
public sealed class ChecksumMismatchException : ProtocolException
{
    public ChecksumMismatchException(string fileName, string expected, string actual)
        : base($"Checksum mismatch for '{fileName}': expected {expected}, computed {actual}.", ErrorCodes.ChecksumMismatch)
    {
        FileName = fileName;
        Expected = expected;
        Actual = actual;
    }

    public string FileName { get; }

    public string Expected { get; }

    public string Actual { get; }
}
