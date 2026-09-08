using System.Text.Json;
using System.Text.Json.Serialization;

namespace NearbyShare.Core.Protocol;

/// <summary>
/// Order-independent polymorphic (de)serializer for the PROTOCOL.md §4 envelope.
/// </summary>
/// <remarks>
/// The envelope is written field-by-field and only the <c>payload</c> is handed
/// back to <see cref="JsonSerializer"/>. Payload POCOs never reference
/// <see cref="Message"/>, so this converter is not re-entrant and the
/// <c>[JsonConverter]</c> attribute on the base class is safe for derived types.
/// </remarks>
public sealed class MessageJsonConverter : JsonConverter<Message>
{
    /// <summary>Options used for the inner <c>payload</c> object only.</summary>
    internal static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public override bool CanConvert(Type typeToConvert) => typeof(Message).IsAssignableFrom(typeToConvert);

    public override Message Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a JSON object for the message envelope but found '{reader.TokenType}'.");
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Message envelope is missing a string 'type' field (PROTOCOL.md §4).");
        }

        string type = typeElement.GetString()!;

        int version = ProtocolConstants.Version;
        if (root.TryGetProperty("v", out JsonElement versionElement))
        {
            if (versionElement.ValueKind == JsonValueKind.Number && versionElement.TryGetInt32(out int parsedVersion))
            {
                version = parsedVersion;
            }
            else if (versionElement.ValueKind == JsonValueKind.String &&
                     int.TryParse(versionElement.GetString(), out int parsedStringVersion))
            {
                version = parsedStringVersion;
            }
            else
            {
                throw new JsonException("Message envelope field 'v' must be an integer (PROTOCOL.md §4).");
            }
        }

        string id = root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : string.Empty;

        JsonElement? payload = root.TryGetProperty("payload", out JsonElement payloadElement)
            ? payloadElement.Clone()
            : null;

        Message message = type switch
        {
            MessageTypes.Hello => new HelloMessage { Payload = DeserializePayload<HelloPayload>(payload, type) },
            MessageTypes.Offer => new OfferMessage { Payload = DeserializePayload<OfferPayload>(payload, type) },
            MessageTypes.Accept => new AcceptMessage { Payload = DeserializePayload<AcceptPayload>(payload, type) },
            MessageTypes.Reject => new RejectMessage { Payload = DeserializePayload<RejectPayload>(payload, type) },
            MessageTypes.Progress => new ProgressMessage { Payload = DeserializePayload<ProgressPayload>(payload, type) },
            MessageTypes.Done => new DoneMessage { Payload = DeserializePayload<DonePayload>(payload, type) },
            MessageTypes.Error => new ErrorMessage { Payload = DeserializePayload<ErrorPayload>(payload, type) },
            MessageTypes.Cancel => new CancelMessage { Payload = DeserializePayload<CancelPayload>(payload, type) },

            // PROTOCOL.md §4 forward-compatibility rule: an unrecognized type is
            // NOT an error at the decoding layer. The caller answers with
            // ERROR{UNSUPPORTED_TYPE} and keeps the connection usable.
            _ => new UnknownMessage(type, payload),
        };

        message.V = version;
        message.Id = id;
        return message;
    }

    public override void Write(Utf8JsonWriter writer, Message value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", value.V);
        writer.WriteString("type", value.Type);
        writer.WriteString("id", value.Id);

        object? payload = value.PayloadObject;
        if (payload is not null)
        {
            writer.WritePropertyName("payload");
            if (payload is JsonElement element)
            {
                element.WriteTo(writer);
            }
            else
            {
                JsonSerializer.Serialize(writer, payload, payload.GetType(), PayloadOptions);
            }
        }

        writer.WriteEndObject();
    }

    private static TPayload DeserializePayload<TPayload>(JsonElement? payload, string type)
        where TPayload : class, new()
    {
        if (payload is null || payload.Value.ValueKind == JsonValueKind.Null)
        {
            throw new JsonException($"Message of type '{type}' is missing its 'payload' object (PROTOCOL.md §4).");
        }

        if (payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Message of type '{type}' has a non-object 'payload' (PROTOCOL.md §4).");
        }

        return payload.Value.Deserialize<TPayload>(PayloadOptions)
               ?? throw new JsonException($"Message of type '{type}' has an undecodable 'payload'.");
    }
}
