using System.Text.Json;
using NearbyShare.Core.Protocol;
using Xunit;

namespace NearbyShare.Core.Tests.Protocol;

/// <summary>
/// The cross-implementation contract test required by PROTOCOL.md §7.
/// </summary>
/// <remarks>
/// <c>Core.Tests/Fixtures</c> holds one JSON example per MVP message type. The
/// Android repository checks in an identical set under its own test resources,
/// and both suites must decode every fixture into the right message type with
/// the right field values. If a field name or casing ever drifts between the
/// Kotlin and C# implementations, these tests fail on the side that drifted.
/// </remarks>
public class FixtureContractTests
{
    private static readonly string FixtureDirectory = LocateFixtureDirectory();

    public static TheoryData<string> FixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(FixtureDirectory, "*.json").OrderBy(p => p))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Fact]
    public void EveryMvpMessageTypeHasAFixture()
    {
        string[] required =
        {
            "HELLO.json", "OFFER.json", "ACCEPT.json", "REJECT.json",
            "PROGRESS.json", "DONE.json", "ERROR.json", "CANCEL.json",
        };

        foreach (string name in required)
        {
            Assert.True(
                File.Exists(Path.Combine(FixtureDirectory, name)),
                $"Missing cross-implementation fixture '{name}' required by PROTOCOL.md §7.");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void EveryFixtureDecodesAndReEncodesToEquivalentJson(string fileName)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, fileName));
        Message decoded = MessageCodec.DecodeBody(bytes);

        Assert.Equal(ProtocolConstants.Version, decoded.V);
        Assert.False(string.IsNullOrWhiteSpace(decoded.Id));

        // An unknown type is expected to survive decoding (PROTOCOL.md §4) but
        // cannot round-trip through our typed payloads.
        if (decoded is UnknownMessage)
        {
            return;
        }

        // Re-encoding must produce JSON the peer would also accept: same type,
        // same id, and a payload with the same field names and values.
        using JsonDocument original = JsonDocument.Parse(bytes);
        using JsonDocument reencoded = JsonDocument.Parse(MessageCodec.EncodeBody(decoded));

        Assert.Equal(
            original.RootElement.GetProperty("type").GetString(),
            reencoded.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            original.RootElement.GetProperty("id").GetString(),
            reencoded.RootElement.GetProperty("id").GetString());

        AssertPayloadFieldsPreserved(
            original.RootElement.GetProperty("payload"),
            reencoded.RootElement.GetProperty("payload"),
            fileName);
    }

    [Fact]
    public void HelloFixtureDecodesWithTheSpecifiedFieldNames()
    {
        var hello = Assert.IsType<HelloMessage>(Load("HELLO.json"));

        Assert.Equal("9f2c1d54-7b3e-4a10-8c6f-2d5e1b7a9c04", hello.Id);
        Assert.Equal("3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95", hello.Payload.DeviceId);
        Assert.Equal("Living Room PC", hello.Payload.DeviceName);
        Assert.Equal(1, hello.Payload.ProtocolVersion);
    }

    [Fact]
    public void OfferFixtureDecodesFilesIncludingOptionalFields()
    {
        var offer = Assert.IsType<OfferMessage>(Load("OFFER.json"));

        Assert.Equal("5c1b1e2a-4f80-4d3b-9a71-c8e0f6b2d451", offer.Payload.TransferId);
        Assert.Equal(2, offer.Payload.Files.Count);

        FileEntry first = offer.Payload.Files[0];
        Assert.Equal("holiday-photo.jpg", first.Name);
        Assert.Equal(2458112, first.Size);
        Assert.Equal("image/jpeg", first.Mime);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", first.Sha256);

        // sha256 is optional per PROTOCOL.md §5; its absence must decode as null,
        // not as an empty string.
        FileEntry second = offer.Payload.Files[1];
        Assert.Equal("notes.txt", second.Name);
        Assert.Equal(1024, second.Size);
        Assert.Null(second.Sha256);

        Assert.Equal(2459136, offer.Payload.TotalBytes);
    }

    [Fact]
    public void AcceptFixtureDecodes()
    {
        var accept = Assert.IsType<AcceptMessage>(Load("ACCEPT.json"));
        Assert.Equal("5c1b1e2a-4f80-4d3b-9a71-c8e0f6b2d451", accept.Payload.TransferId);
    }

    [Fact]
    public void RejectFixtureDecodesWithReason()
    {
        var reject = Assert.IsType<RejectMessage>(Load("REJECT.json"));
        Assert.Equal("5c1b1e2a-4f80-4d3b-9a71-c8e0f6b2d451", reject.Payload.TransferId);
        Assert.Equal("Declined by the user.", reject.Payload.Reason);
    }

    [Fact]
    public void ProgressFixtureDecodesCounters()
    {
        var progress = Assert.IsType<ProgressMessage>(Load("PROGRESS.json"));
        Assert.Equal("5c1b1e2a-4f80-4d3b-9a71-c8e0f6b2d451", progress.Payload.TransferId);
        Assert.Equal(0, progress.Payload.FileIndex);
        Assert.Equal(4096, progress.Payload.BytesTransferred);
        Assert.Equal(2458112, progress.Payload.TotalBytes);
    }

    [Fact]
    public void DoneFixtureDecodesTheAllSentinel()
    {
        var done = Assert.IsType<DoneMessage>(Load("DONE.json"));
        Assert.True(done.Payload.FileIndex.IsAll);
        Assert.Equal("all", done.Payload.FileIndex.ToString());
    }

    [Fact]
    public void DoneFixtureDecodesANumericFileIndex()
    {
        var done = Assert.IsType<DoneMessage>(Load("DONE_FILE.json"));
        Assert.False(done.Payload.FileIndex.IsAll);
        Assert.Equal(0, done.Payload.FileIndex.Index);
    }

    [Fact]
    public void ErrorFixtureDecodesTransferScopedError()
    {
        var error = Assert.IsType<ErrorMessage>(Load("ERROR.json"));
        Assert.Equal("5c1b1e2a-4f80-4d3b-9a71-c8e0f6b2d451", error.Payload.TransferId);
        Assert.Equal(ErrorCodes.ChecksumMismatch, error.Payload.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Payload.Message));
    }

    [Fact]
    public void ErrorFixtureDecodesWithoutATransferId()
    {
        // The ERROR that answers an unrecognized type (PROTOCOL.md §4) is not
        // transfer-scoped, so transferId is absent.
        var error = Assert.IsType<ErrorMessage>(Load("ERROR_UNSUPPORTED_TYPE.json"));
        Assert.Null(error.Payload.TransferId);
        Assert.Equal(ErrorCodes.UnsupportedType, error.Payload.Code);
    }

    [Fact]
    public void CancelFixtureDecodes()
    {
        var cancel = Assert.IsType<CancelMessage>(Load("CANCEL.json"));
        Assert.Equal("5c1b1e2a-4f80-4d3b-9a71-c8e0f6b2d451", cancel.Payload.TransferId);
    }

    [Fact]
    public void UnknownTypeFixtureDecodesToUnknownMessageRatherThanThrowing()
    {
        var unknown = Assert.IsType<UnknownMessage>(Load("UNKNOWN_TYPE.json"));
        Assert.Equal("CLIPBOARD_OFFER", unknown.UnknownType);
        Assert.Equal("7d4a2b19-6c35-4e82-9f01-a3b5c8d7e206", unknown.Id);
        Assert.NotNull(unknown.RawPayload);
    }

    private static Message Load(string fileName) =>
        MessageCodec.DecodeBody(File.ReadAllBytes(Path.Combine(FixtureDirectory, fileName)));

    /// <summary>
    /// Asserts every field present in the fixture's payload survived the
    /// decode/encode round trip with the same name and value.
    /// </summary>
    private static void AssertPayloadFieldsPreserved(JsonElement expected, JsonElement actual, string fileName)
    {
        foreach (JsonProperty property in expected.EnumerateObject())
        {
            Assert.True(
                actual.TryGetProperty(property.Name, out JsonElement actualValue),
                $"{fileName}: field '{property.Name}' was lost in the round trip — the C# model does not match PROTOCOL.md §5.");

            Assert.Equal(property.Value.ValueKind, actualValue.ValueKind);

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    AssertPayloadFieldsPreserved(property.Value, actualValue, fileName);
                    break;

                case JsonValueKind.Array:
                    Assert.Equal(property.Value.GetArrayLength(), actualValue.GetArrayLength());
                    for (int i = 0; i < property.Value.GetArrayLength(); i++)
                    {
                        AssertPayloadFieldsPreserved(property.Value[i], actualValue[i], fileName);
                    }

                    break;

                default:
                    Assert.Equal(property.Value.ToString(), actualValue.ToString());
                    break;
            }
        }
    }

    /// <summary>
    /// Finds the fixtures next to the test binary, falling back to the source tree
    /// so the tests also run from an IDE with a different working directory.
    /// </summary>
    private static string LocateFixtureDirectory()
    {
        string candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string source = Path.Combine(directory.FullName, "Core.Tests", "Fixtures");
            if (Directory.Exists(source))
            {
                return source;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Core.Tests/Fixtures directory.");
    }
}
