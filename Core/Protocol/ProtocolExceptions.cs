namespace NearbyShare.Core.Protocol;

/// <summary>Base type for every wire-protocol violation detected by this implementation.</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message, string errorCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The <see cref="ErrorCodes"/> value to report in an <c>ERROR</c> message.</summary>
    public string ErrorCode { get; }
}

/// <summary>
/// The frame's length prefix exceeded <see cref="ProtocolConstants.MaxPayloadBytes"/>,
/// or was zero. PROTOCOL.md §3 requires the connection to be aborted with an
/// <c>ERROR</c> rather than attempting the allocation.
/// </summary>
public sealed class MessageTooLargeException : ProtocolException
{
    public MessageTooLargeException(long declaredLength)
        : base($"Declared control-message length {declaredLength} exceeds the {ProtocolConstants.MaxPayloadBytes} byte (1 MiB) maximum from PROTOCOL.md §3.",
            ErrorCodes.MessageTooLarge)
    {
        DeclaredLength = declaredLength;
    }

    public long DeclaredLength { get; }
}

/// <summary>The frame was well-formed but its JSON body could not be decoded into a known envelope.</summary>
public sealed class MalformedMessageException : ProtocolException
{
    public MalformedMessageException(string message, Exception? innerException = null)
        : base(message, ErrorCodes.ProtocolError, innerException)
    {
    }
}

/// <summary>
/// The stream ended part-way through a length prefix or a payload. Distinct from a
/// clean end-of-stream at a frame boundary, which <see cref="MessageCodec.ReadAsync"/>
/// reports by returning <c>null</c>.
/// </summary>
public sealed class IncompleteFrameException : ProtocolException
{
    public IncompleteFrameException(int bytesRead, int bytesExpected)
        : base($"Stream ended after {bytesRead} of {bytesExpected} expected bytes; the frame is truncated.",
            ErrorCodes.ProtocolError)
    {
        BytesRead = bytesRead;
        BytesExpected = bytesExpected;
    }

    public int BytesRead { get; }

    public int BytesExpected { get; }
}

/// <summary>The peer announced an envelope <c>v</c> this implementation does not support (PROTOCOL.md §6).</summary>
public sealed class UnsupportedProtocolVersionException : ProtocolException
{
    public UnsupportedProtocolVersionException(int version)
        : base($"Peer used protocol version {version}; this implementation supports version {ProtocolConstants.Version}.",
            ErrorCodes.UnsupportedVersion)
    {
        Version = version;
    }

    public int Version { get; }
}
