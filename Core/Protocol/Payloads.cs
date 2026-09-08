using System.Text.Json;
using System.Text.Json.Serialization;

namespace NearbyShare.Core.Protocol;

// -----------------------------------------------------------------------------
// Payload types. Every [JsonPropertyName] here is part of the wire contract in
// PROTOCOL.md §5 and is mirrored by the Kotlin implementation. Do not rename.
// -----------------------------------------------------------------------------

/// <summary>PROTOCOL.md §5 <c>HELLO</c> payload.</summary>
public sealed class HelloPayload
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
}

/// <summary>One entry of the <c>files</c> array in an <c>OFFER</c> payload (PROTOCOL.md §5).</summary>
public sealed class FileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>MIME type. Optional in practice; omitted from JSON when null.</summary>
    [JsonPropertyName("mime")]
    public string? Mime { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the file contents. Optional per PROTOCOL.md §5;
    /// when present the receiver verifies integrity after writing to disk.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

/// <summary>PROTOCOL.md §5 <c>OFFER</c> payload.</summary>
public sealed class OfferPayload
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<FileEntry> Files { get; set; } = new();

    /// <summary>Sum of all declared file sizes. Not a wire field.</summary>
    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>PROTOCOL.md §5 <c>ACCEPT</c> payload.</summary>
public sealed class AcceptPayload
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;
}

/// <summary>PROTOCOL.md §5 <c>REJECT</c> payload.</summary>
public sealed class RejectPayload
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>PROTOCOL.md §5 <c>PROGRESS</c> payload.</summary>
public sealed class ProgressPayload
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("fileIndex")]
    public int FileIndex { get; set; }

    [JsonPropertyName("bytesTransferred")]
    public long BytesTransferred { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}

/// <summary>PROTOCOL.md §5 <c>DONE</c> payload.</summary>
public sealed class DonePayload
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    /// <summary>
    /// Either a zero-based file index (JSON number) or the string <c>"all"</c>
    /// once every file is complete — see PROTOCOL.md §5.
    /// </summary>
    [JsonPropertyName("fileIndex")]
    public FileIndex FileIndex { get; set; } = FileIndex.All;
}

/// <summary>PROTOCOL.md §5 <c>ERROR</c> payload.</summary>
public sealed class ErrorPayload
{
    /// <summary>
    /// Nullable: the <c>ERROR</c> emitted for an unrecognized message type
    /// (PROTOCOL.md §4) is not necessarily transfer-scoped, so the field is
    /// omitted from JSON rather than serialized as null.
    /// </summary>
    [JsonPropertyName("transferId")]
    public string? TransferId { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>PROTOCOL.md §5 <c>CANCEL</c> payload.</summary>
public sealed class CancelPayload
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;
}

/// <summary>
/// The union type of the <c>DONE</c> payload's <c>fileIndex</c> field: either a
/// zero-based index or the sentinel string <c>"all"</c> (PROTOCOL.md §5).
/// </summary>
[JsonConverter(typeof(FileIndexJsonConverter))]
public readonly struct FileIndex : IEquatable<FileIndex>
{
    /// <summary>The literal used on the wire for the "every file" sentinel.</summary>
    public const string AllToken = "all";

    private FileIndex(bool isAll, int index)
    {
        IsAll = isAll;
        Index = index;
    }

    /// <summary>True when this represents the <c>"all"</c> sentinel.</summary>
    public bool IsAll { get; }

    /// <summary>The zero-based file index. Meaningless when <see cref="IsAll"/> is true.</summary>
    public int Index { get; }

    /// <summary>The <c>"all"</c> sentinel.</summary>
    public static FileIndex All => new(true, -1);

    /// <summary>A concrete zero-based file index.</summary>
    public static FileIndex Of(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "File index must be non-negative.");
        }

        return new FileIndex(false, index);
    }

    public bool Equals(FileIndex other) => IsAll == other.IsAll && (IsAll || Index == other.Index);

    public override bool Equals(object? obj) => obj is FileIndex other && Equals(other);

    public override int GetHashCode() => IsAll ? -1 : Index;

    public override string ToString() => IsAll ? AllToken : Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(FileIndex left, FileIndex right) => left.Equals(right);

    public static bool operator !=(FileIndex left, FileIndex right) => !left.Equals(right);
}

/// <summary>
/// Reads/writes <see cref="FileIndex"/> as either a JSON number or the string
/// <c>"all"</c>.
/// </summary>
public sealed class FileIndexJsonConverter : JsonConverter<FileIndex>
{
    public override FileIndex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return FileIndex.Of(reader.GetInt32());

            case JsonTokenType.String:
            {
                string? value = reader.GetString();
                if (string.Equals(value, FileIndex.AllToken, StringComparison.Ordinal))
                {
                    return FileIndex.All;
                }

                // Be liberal in what we accept: a numeric string still identifies a file.
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed >= 0)
                {
                    return FileIndex.Of(parsed);
                }

                throw new JsonException($"Invalid fileIndex value '{value}'; expected a number or \"all\".");
            }

            default:
                throw new JsonException($"Invalid fileIndex token '{reader.TokenType}'; expected a number or \"all\".");
        }
    }

    public override void Write(Utf8JsonWriter writer, FileIndex value, JsonSerializerOptions options)
    {
        if (value.IsAll)
        {
            writer.WriteStringValue(FileIndex.AllToken);
        }
        else
        {
            writer.WriteNumberValue(value.Index);
        }
    }
}
