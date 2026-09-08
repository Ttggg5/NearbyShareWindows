namespace NearbyShare.Core.Protocol;

/// <summary>
/// What a receiver should do with an inbound envelope before acting on it.
/// </summary>
public enum EnvelopeDisposition
{
    /// <summary>The message is understood; hand it to the transfer state machine.</summary>
    Accept,

    /// <summary>
    /// The <c>type</c> is unrecognized. Reply <c>ERROR{UNSUPPORTED_TYPE}</c> and
    /// leave the connection in a valid state (PROTOCOL.md §4).
    /// </summary>
    UnsupportedType,

    /// <summary>
    /// The envelope <c>v</c> is not supported. Reply
    /// <c>ERROR{UNSUPPORTED_VERSION}</c> and close the connection (PROTOCOL.md §4/§6).
    /// </summary>
    UnsupportedVersion,
}

/// <summary>The outcome of inspecting an inbound envelope.</summary>
/// <param name="Disposition">What the receiver should do.</param>
/// <param name="Message">The decoded message.</param>
/// <param name="Response">
/// The <c>ERROR</c> to send back, or <c>null</c> when the message is understood.
/// </param>
/// <param name="ShouldCloseConnection">
/// True when the protocol requires the connection to be closed after sending
/// <paramref name="Response"/>.
/// </param>
public readonly record struct EnvelopeInspection(
    EnvelopeDisposition Disposition,
    Message Message,
    ErrorMessage? Response,
    bool ShouldCloseConnection);

/// <summary>
/// Implements the envelope-level rules of PROTOCOL.md §4 and §6 that every
/// receiver must apply before dispatching a message: version gating and the
/// forward-compatibility rule for unrecognized <c>type</c> values.
/// </summary>
public static class MessageDispatcher
{
    /// <summary>
    /// Inspects a decoded envelope and reports whether it can be acted on, plus the
    /// <c>ERROR</c> that must be sent back if not.
    /// </summary>
    public static EnvelopeInspection Inspect(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // PROTOCOL.md §6: reject an unsupported major version rather than guessing.
        // Checked first — we cannot trust the meaning of `type` under an unknown
        // wire format.
        if (message.V != ProtocolConstants.Version)
        {
            return new EnvelopeInspection(
                EnvelopeDisposition.UnsupportedVersion,
                message,
                ErrorMessage.Create(
                    transferId: null,
                    code: ErrorCodes.UnsupportedVersion,
                    message: $"Protocol version {message.V} is not supported; this device speaks version {ProtocolConstants.Version}.",
                    envelopeId: message.Id),
                ShouldCloseConnection: true);
        }

        // PROTOCOL.md §4: an unrecognized type must NOT crash or abruptly
        // disconnect. Answer ERROR{UNSUPPORTED_TYPE} and keep the connection
        // valid for further messages.
        if (message is UnknownMessage unknown)
        {
            return new EnvelopeInspection(
                EnvelopeDisposition.UnsupportedType,
                message,
                ErrorMessage.Create(
                    transferId: null,
                    code: ErrorCodes.UnsupportedType,
                    message: $"Message type '{unknown.UnknownType}' is not supported by this device.",
                    envelopeId: message.Id),
                ShouldCloseConnection: false);
        }

        return new EnvelopeInspection(EnvelopeDisposition.Accept, message, Response: null, ShouldCloseConnection: false);
    }

    /// <summary>
    /// Reads the next message from <paramref name="stream"/>, applies
    /// <see cref="Inspect"/>, and — for anything the receiver cannot act on —
    /// writes the required <c>ERROR</c> back before returning. Unsupported types
    /// are transparently skipped so the caller only ever sees messages it
    /// understands; the connection stays usable, exactly as PROTOCOL.md §4 requires.
    /// </summary>
    /// <returns>
    /// The next understood message, or <c>null</c> if the peer closed the
    /// connection or the connection must be closed (unsupported version).
    /// </returns>
    public static async Task<Message?> ReadDispatchableAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Message? message = await MessageCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return null;
            }

            EnvelopeInspection inspection = Inspect(message);
            if (inspection.Disposition == EnvelopeDisposition.Accept)
            {
                return message;
            }

            if (inspection.Response is not null)
            {
                await MessageCodec.WriteAsync(stream, inspection.Response, cancellationToken).ConfigureAwait(false);
            }

            if (inspection.ShouldCloseConnection)
            {
                throw new UnsupportedProtocolVersionException(message.V);
            }

            // Unsupported type: loop and read the next message.
        }
    }
}
