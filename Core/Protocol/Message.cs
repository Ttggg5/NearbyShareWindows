using System.Text.Json;
using System.Text.Json.Serialization;

namespace NearbyShare.Core.Protocol;

/// <summary>
/// The control-message envelope from PROTOCOL.md §4:
/// <c>{ "v": 1, "type": "OFFER", "id": "...", "payload": { ... } }</c>.
/// </summary>
/// <remarks>
/// Polymorphism is implemented with a hand-written <see cref="MessageJsonConverter"/>
/// rather than <c>[JsonDerivedType]</c>. The built-in <c>System.Text.Json</c>
/// polymorphic reader on .NET 8 requires the type discriminator to be the FIRST
/// property of the JSON object (<c>AllowOutOfOrderMetadataProperties</c> only
/// arrived in .NET 9), but PROTOCOL.md §4 shows <c>"v"</c> ahead of <c>"type"</c>
/// and places no ordering requirement on the wire — the Kotlin side is free to
/// emit either order. The custom converter is order-independent and also gives
/// us the forward-compatibility behaviour required by PROTOCOL.md §4: an
/// unrecognized <c>type</c> decodes into <see cref="UnknownMessage"/> instead of
/// throwing, so the caller can answer <c>ERROR{UNSUPPORTED_TYPE}</c> and keep the
/// connection alive.
/// </remarks>
[JsonConverter(typeof(MessageJsonConverter))]
public abstract class Message
{
    /// <summary>Envelope <c>v</c>: the protocol version this message was constructed under.</summary>
    [JsonPropertyName("v")]
    [JsonPropertyOrder(0)]
    public int V { get; set; } = ProtocolConstants.Version;

    /// <summary>Envelope <c>type</c> discriminator. Read-only; fixed per concrete message class.</summary>
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public abstract string Type { get; }

    /// <summary>
    /// Envelope <c>id</c>: the transfer UUID for transfer-scoped messages, or an
    /// arbitrary per-message UUID for <c>HELLO</c>.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(2)]
    public string Id { get; set; } = string.Empty;

    /// <summary>The payload object, boxed. Used by generic plumbing and tests.</summary>
    [JsonIgnore]
    public abstract object? PayloadObject { get; }
}

/// <summary>A <see cref="Message"/> with a strongly typed <c>payload</c>.</summary>
/// <typeparam name="TPayload">The payload type for this message kind.</typeparam>
public abstract class Message<TPayload> : Message
    where TPayload : class, new()
{
    [JsonPropertyName("payload")]
    [JsonPropertyOrder(3)]
    public TPayload Payload { get; set; } = new();

    [JsonIgnore]
    public override object? PayloadObject => Payload;
}

/// <summary>PROTOCOL.md §5 <c>HELLO</c>.</summary>
public sealed class HelloMessage : Message<HelloPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Hello;

    public static HelloMessage Create(string deviceId, string deviceName) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Payload = new HelloPayload
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            ProtocolVersion = ProtocolConstants.Version,
        },
    };
}

/// <summary>PROTOCOL.md §5 <c>OFFER</c>.</summary>
public sealed class OfferMessage : Message<OfferPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Offer;

    public static OfferMessage Create(string transferId, IEnumerable<FileEntry> files) => new()
    {
        Id = transferId,
        Payload = new OfferPayload { TransferId = transferId, Files = files.ToList() },
    };
}

/// <summary>PROTOCOL.md §5 <c>ACCEPT</c>.</summary>
public sealed class AcceptMessage : Message<AcceptPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Accept;

    public static AcceptMessage Create(string transferId) => new()
    {
        Id = transferId,
        Payload = new AcceptPayload { TransferId = transferId },
    };
}

/// <summary>PROTOCOL.md §5 <c>REJECT</c>.</summary>
public sealed class RejectMessage : Message<RejectPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Reject;

    public static RejectMessage Create(string transferId, string? reason) => new()
    {
        Id = transferId,
        Payload = new RejectPayload { TransferId = transferId, Reason = reason },
    };
}

/// <summary>PROTOCOL.md §5 <c>PROGRESS</c>.</summary>
public sealed class ProgressMessage : Message<ProgressPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Progress;

    public static ProgressMessage Create(string transferId, int fileIndex, long bytesTransferred, long totalBytes) => new()
    {
        Id = transferId,
        Payload = new ProgressPayload
        {
            TransferId = transferId,
            FileIndex = fileIndex,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
        },
    };
}

/// <summary>PROTOCOL.md §5 <c>DONE</c>.</summary>
public sealed class DoneMessage : Message<DonePayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Done;

    public static DoneMessage Create(string transferId, FileIndex fileIndex) => new()
    {
        Id = transferId,
        Payload = new DonePayload { TransferId = transferId, FileIndex = fileIndex },
    };

    /// <summary>The terminal <c>DONE</c> with <c>"fileIndex": "all"</c>.</summary>
    public static DoneMessage CreateAll(string transferId) => Create(transferId, FileIndex.All);
}

/// <summary>PROTOCOL.md §5 <c>ERROR</c>.</summary>
public sealed class ErrorMessage : Message<ErrorPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Error;

    public static ErrorMessage Create(string? transferId, string code, string? message, string? envelopeId = null) => new()
    {
        // PROTOCOL.md §4: the ERROR answering an unrecognized message reuses that
        // message's envelope id, so the peer can correlate it.
        Id = envelopeId ?? transferId ?? Guid.NewGuid().ToString(),
        Payload = new ErrorPayload { TransferId = transferId, Code = code, Message = message },
    };
}

/// <summary>PROTOCOL.md §5 <c>CANCEL</c>.</summary>
public sealed class CancelMessage : Message<CancelPayload>
{
    [JsonPropertyName("type")]
    [JsonPropertyOrder(1)]
    public override string Type => MessageTypes.Cancel;

    public static CancelMessage Create(string transferId) => new()
    {
        Id = transferId,
        Payload = new CancelPayload { TransferId = transferId },
    };
}

/// <summary>
/// A well-formed envelope whose <c>type</c> this implementation does not
/// recognize. Produced instead of throwing so that callers can honour the
/// forward-compatibility rule in PROTOCOL.md §4: answer
/// <c>ERROR{UNSUPPORTED_TYPE}</c> and leave the connection usable.
/// </summary>
public sealed class UnknownMessage : Message
{
    public UnknownMessage(string type, JsonElement? payload = null)
    {
        UnknownType = type;
        RawPayload = payload;
    }

    /// <summary>The unrecognized <c>type</c> string exactly as it appeared on the wire.</summary>
    public string UnknownType { get; }

    [JsonPropertyName("type")]
    public override string Type => UnknownType;

    /// <summary>The undecoded <c>payload</c> element, if the envelope had one.</summary>
    [JsonIgnore]
    public JsonElement? RawPayload { get; }

    [JsonIgnore]
    public override object? PayloadObject => RawPayload;
}
