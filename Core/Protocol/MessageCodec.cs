using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NearbyShare.Core.Protocol;

/// <summary>
/// Implements the PROTOCOL.md §3 framing:
/// <c>[4 bytes: big-endian uint32 length N][N bytes: UTF-8 JSON payload]</c>,
/// with the 1 MiB payload ceiling enforced on both read and write.
/// </summary>
public static class MessageCodec
{
    /// <summary>The canonical serializer options for control messages.</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = false,
        };
        options.Converters.Add(new MessageJsonConverter());
        return options;
    }

    // ---------------------------------------------------------------------
    // Buffer-level API
    // ---------------------------------------------------------------------

    /// <summary>Serializes <paramref name="message"/> to its UTF-8 JSON body (no length prefix).</summary>
    /// <exception cref="MessageTooLargeException">The body exceeds 1 MiB.</exception>
    public static byte[] EncodeBody(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] body = JsonSerializer.SerializeToUtf8Bytes<Message>(message, JsonOptions);
        if (body.Length > ProtocolConstants.MaxPayloadBytes)
        {
            throw new MessageTooLargeException(body.Length);
        }

        return body;
    }

    /// <summary>Serializes <paramref name="message"/> to a complete frame (length prefix + body).</summary>
    public static byte[] EncodeFrame(Message message)
    {
        byte[] body = EncodeBody(message);
        var frame = new byte[ProtocolConstants.LengthPrefixBytes + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, ProtocolConstants.LengthPrefixBytes);
        return frame;
    }

    /// <summary>Decodes a UTF-8 JSON control-message body (no length prefix).</summary>
    /// <exception cref="MalformedMessageException">The body is not a decodable envelope.</exception>
    public static Message DecodeBody(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            throw new MalformedMessageException("Control-message body is empty.");
        }

        if (body.Length > ProtocolConstants.MaxPayloadBytes)
        {
            throw new MessageTooLargeException(body.Length);
        }

        try
        {
            return JsonSerializer.Deserialize<Message>(body, JsonOptions)
                   ?? throw new MalformedMessageException("Control-message body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new MalformedMessageException($"Control-message body is not valid protocol JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Decodes a complete frame (length prefix + body).</summary>
    public static Message DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < ProtocolConstants.LengthPrefixBytes)
        {
            throw new IncompleteFrameException(frame.Length, ProtocolConstants.LengthPrefixBytes);
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(frame);
        ValidateDeclaredLength(declared);

        int bodyLength = (int)declared;
        int available = frame.Length - ProtocolConstants.LengthPrefixBytes;
        if (available < bodyLength)
        {
            throw new IncompleteFrameException(available, bodyLength);
        }

        return DecodeBody(frame.Slice(ProtocolConstants.LengthPrefixBytes, bodyLength));
    }

    // ---------------------------------------------------------------------
    // Stream-level API
    // ---------------------------------------------------------------------

    /// <summary>Writes one framed control message to <paramref name="stream"/> and flushes.</summary>
    public static async Task WriteAsync(Stream stream, Message message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] frame = EncodeFrame(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed control message from <paramref name="stream"/>.
    /// </summary>
    /// <returns>
    /// The decoded message, or <c>null</c> if the stream ended cleanly at a frame
    /// boundary (i.e. the peer closed the connection between messages).
    /// </returns>
    /// <exception cref="IncompleteFrameException">The stream ended mid-frame.</exception>
    /// <exception cref="MessageTooLargeException">The length prefix exceeded 1 MiB, or was zero.</exception>
    /// <exception cref="MalformedMessageException">The body was not a decodable envelope.</exception>
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] prefix = new byte[ProtocolConstants.LengthPrefixBytes];
        int prefixRead = await ReadAtLeastAsync(stream, prefix, cancellationToken).ConfigureAwait(false);

        if (prefixRead == 0)
        {
            // Clean end-of-stream at a frame boundary.
            return null;
        }

        if (prefixRead < ProtocolConstants.LengthPrefixBytes)
        {
            throw new IncompleteFrameException(prefixRead, ProtocolConstants.LengthPrefixBytes);
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        ValidateDeclaredLength(declared);

        int bodyLength = (int)declared;
        byte[] body = new byte[bodyLength];
        int bodyRead = await ReadAtLeastAsync(stream, body, cancellationToken).ConfigureAwait(false);
        if (bodyRead < bodyLength)
        {
            throw new IncompleteFrameException(bodyRead, bodyLength);
        }

        return DecodeBody(body);
    }

    /// <summary>
    /// Like <see cref="ReadAsync"/> but throws <see cref="IncompleteFrameException"/>
    /// instead of returning <c>null</c> when the peer closes the connection — used
    /// where the protocol requires a message to arrive.
    /// </summary>
    public static async Task<Message> ReadRequiredAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Message? message = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        return message ?? throw new IncompleteFrameException(0, ProtocolConstants.LengthPrefixBytes);
    }

    private static void ValidateDeclaredLength(uint declared)
    {
        // Any length past 1 MiB must abort the connection rather than allocate
        // (PROTOCOL.md §3).
        if (declared > ProtocolConstants.MaxPayloadBytes)
        {
            throw new MessageTooLargeException(declared);
        }

        // A zero-length body can never be a valid JSON object.
        if (declared == 0)
        {
            throw new MalformedMessageException("Frame declared a zero-length control-message body.");
        }
    }

    /// <summary>Fills <paramref name="buffer"/>, returning early only at end-of-stream.</summary>
    private static async Task<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
