using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NearbyShare.Core.Protocol;
using Xunit;

namespace NearbyShare.Core.Tests.Protocol;

/// <summary>
/// Round-trip and framing tests for PROTOCOL.md §3/§4/§5.
/// </summary>
public class MessageCodecTests
{
    public static TheoryData<Message> AllMvpMessageTypes() => new()
    {
        HelloMessage.Create("3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95", "Living Room PC"),
        OfferMessage.Create("t-1", new[]
        {
            new FileEntry { Name = "a.jpg", Size = 2458112, Mime = "image/jpeg", Sha256 = new string('a', 64) },
            new FileEntry { Name = "b.txt", Size = 1024, Mime = "text/plain" },
        }),
        AcceptMessage.Create("t-1"),
        RejectMessage.Create("t-1", "Declined by the user."),
        ProgressMessage.Create("t-1", 0, 4096, 2458112),
        DoneMessage.Create("t-1", FileIndex.Of(0)),
        DoneMessage.CreateAll("t-1"),
        ErrorMessage.Create("t-1", ErrorCodes.ChecksumMismatch, "checksum did not match"),
        CancelMessage.Create("t-1"),
    };

    [Theory]
    [MemberData(nameof(AllMvpMessageTypes))]
    public void EncodeDecodeFrame_RoundTripsEveryMvpMessageType(Message original)
    {
        byte[] frame = MessageCodec.EncodeFrame(original);
        Message decoded = MessageCodec.DecodeFrame(frame);

        Assert.Equal(original.GetType(), decoded.GetType());
        Assert.Equal(original.Type, decoded.Type);
        Assert.Equal(original.Id, decoded.Id);
        Assert.Equal(ProtocolConstants.Version, decoded.V);

        // Comparing the re-encoded JSON proves every payload field survived, not
        // just the envelope.
        Assert.Equal(
            Encoding.UTF8.GetString(MessageCodec.EncodeBody(original)),
            Encoding.UTF8.GetString(MessageCodec.EncodeBody(decoded)));
    }

    [Fact]
    public void EncodeFrame_UsesFourByteBigEndianLengthPrefix()
    {
        Message message = AcceptMessage.Create("t-1");
        byte[] body = MessageCodec.EncodeBody(message);
        byte[] frame = MessageCodec.EncodeFrame(message);

        Assert.Equal(ProtocolConstants.LengthPrefixBytes + body.Length, frame.Length);
        Assert.Equal((uint)body.Length, BinaryPrimitives.ReadUInt32BigEndian(frame));

        // Big-endian means the most significant byte comes first; for a body well
        // under 64 KiB the first two bytes must be zero.
        Assert.Equal(0, frame[0]);
        Assert.Equal(0, frame[1]);
    }

    [Fact]
    public void EncodeBody_EmitsExactlyTheProtocolFieldNames()
    {
        Message message = ProgressMessage.Create("t-1", 3, 4096, 2458112);
        using JsonDocument document = JsonDocument.Parse(MessageCodec.EncodeBody(message));
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("PROGRESS", root.GetProperty("type").GetString());
        Assert.Equal("t-1", root.GetProperty("id").GetString());

        JsonElement payload = root.GetProperty("payload");
        Assert.Equal("t-1", payload.GetProperty("transferId").GetString());
        Assert.Equal(3, payload.GetProperty("fileIndex").GetInt32());
        Assert.Equal(4096, payload.GetProperty("bytesTransferred").GetInt64());
        Assert.Equal(2458112, payload.GetProperty("totalBytes").GetInt64());
    }

    [Fact]
    public void EncodeBody_OmitsOptionalOfferFieldsWhenNull()
    {
        Message message = OfferMessage.Create("t-1", new[] { new FileEntry { Name = "a.bin", Size = 10 } });
        using JsonDocument document = JsonDocument.Parse(MessageCodec.EncodeBody(message));
        JsonElement file = document.RootElement.GetProperty("payload").GetProperty("files")[0];

        Assert.Equal("a.bin", file.GetProperty("name").GetString());
        Assert.Equal(10, file.GetProperty("size").GetInt64());

        // PROTOCOL.md §5 makes sha256 optional; emitting an explicit null would
        // force the Kotlin side to model a nullable it does not need to.
        Assert.False(file.TryGetProperty("sha256", out _));
        Assert.False(file.TryGetProperty("mime", out _));
    }

    [Fact]
    public void DoneMessage_SerializesAllSentinelAsTheStringAll()
    {
        using JsonDocument document = JsonDocument.Parse(MessageCodec.EncodeBody(DoneMessage.CreateAll("t-1")));
        JsonElement fileIndex = document.RootElement.GetProperty("payload").GetProperty("fileIndex");

        Assert.Equal(JsonValueKind.String, fileIndex.ValueKind);
        Assert.Equal("all", fileIndex.GetString());
    }

    [Fact]
    public void DoneMessage_SerializesConcreteIndexAsANumber()
    {
        using JsonDocument document = JsonDocument.Parse(MessageCodec.EncodeBody(DoneMessage.Create("t-1", FileIndex.Of(2))));
        JsonElement fileIndex = document.RootElement.GetProperty("payload").GetProperty("fileIndex");

        Assert.Equal(JsonValueKind.Number, fileIndex.ValueKind);
        Assert.Equal(2, fileIndex.GetInt32());
    }

    [Fact]
    public void DecodeBody_IsIndependentOfEnvelopePropertyOrder()
    {
        // PROTOCOL.md §4's own example puts "v" before "type". System.Text.Json's
        // built-in polymorphism on .NET 8 would reject that, which is exactly why
        // the codec uses a hand-written converter.
        const string typeLast = """
            { "v": 1, "id": "t-1", "payload": { "transferId": "t-1" }, "type": "ACCEPT" }
            """;

        Message decoded = MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(typeLast));

        AcceptMessage accept = Assert.IsType<AcceptMessage>(decoded);
        Assert.Equal("t-1", accept.Payload.TransferId);
    }

    // ------------------------------------------------------------------
    // Framing edge cases (PROTOCOL.md §3)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_ReturnsNullOnCleanEndOfStreamAtFrameBoundary()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        Assert.Null(await MessageCodec.ReadAsync(stream));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadAsync_ThrowsOnTruncatedLengthPrefix(int prefixBytesAvailable)
    {
        byte[] frame = MessageCodec.EncodeFrame(AcceptMessage.Create("t-1"));
        using var stream = new MemoryStream(frame[..prefixBytesAvailable]);

        IncompleteFrameException ex = await Assert.ThrowsAsync<IncompleteFrameException>(
            () => MessageCodec.ReadAsync(stream));

        Assert.Equal(prefixBytesAvailable, ex.BytesRead);
        Assert.Equal(ProtocolConstants.LengthPrefixBytes, ex.BytesExpected);
    }

    [Fact]
    public async Task ReadAsync_ThrowsOnTruncatedPayload()
    {
        byte[] frame = MessageCodec.EncodeFrame(AcceptMessage.Create("t-1"));

        // Keep the whole prefix but drop the last five payload bytes.
        using var stream = new MemoryStream(frame[..^5]);

        IncompleteFrameException ex = await Assert.ThrowsAsync<IncompleteFrameException>(
            () => MessageCodec.ReadAsync(stream));

        Assert.Equal(frame.Length - ProtocolConstants.LengthPrefixBytes, ex.BytesExpected);
        Assert.Equal(ex.BytesExpected - 5, ex.BytesRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsLengthPrefixAboveOneMebibyteWithoutAllocating()
    {
        byte[] prefix = new byte[ProtocolConstants.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, ProtocolConstants.MaxPayloadBytes + 1);

        // Deliberately no payload follows: rejection must happen from the prefix
        // alone, before the receiver would try to allocate or read the body.
        using var stream = new MemoryStream(prefix);

        MessageTooLargeException ex = await Assert.ThrowsAsync<MessageTooLargeException>(
            () => MessageCodec.ReadAsync(stream));

        Assert.Equal(ProtocolConstants.MaxPayloadBytes + 1L, ex.DeclaredLength);
        Assert.Equal(ErrorCodes.MessageTooLarge, ex.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_RejectsMaximumUInt32LengthPrefix()
    {
        byte[] prefix = { 0xFF, 0xFF, 0xFF, 0xFF };
        using var stream = new MemoryStream(prefix);

        MessageTooLargeException ex = await Assert.ThrowsAsync<MessageTooLargeException>(
            () => MessageCodec.ReadAsync(stream));

        Assert.Equal(uint.MaxValue, ex.DeclaredLength);
    }

    [Fact]
    public async Task ReadAsync_AcceptsAPayloadExactlyAtTheOneMebibyteLimit()
    {
        // A message whose body is exactly 1 MiB is legal; only "exceeding" is not.
        var padded = RejectMessage.Create("t-1", new string('x', 16));
        byte[] baseline = MessageCodec.EncodeBody(padded);
        int padding = ProtocolConstants.MaxPayloadBytes - baseline.Length + 16;
        padded = RejectMessage.Create("t-1", new string('x', padding));

        byte[] body = MessageCodec.EncodeBody(padded);
        Assert.Equal(ProtocolConstants.MaxPayloadBytes, body.Length);

        byte[] frame = new byte[ProtocolConstants.LengthPrefixBytes + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, ProtocolConstants.LengthPrefixBytes);

        using var stream = new MemoryStream(frame);
        Message? decoded = await MessageCodec.ReadAsync(stream);
        Assert.IsType<RejectMessage>(decoded);
    }

    [Fact]
    public async Task ReadAsync_RejectsZeroLengthFrame()
    {
        byte[] prefix = { 0, 0, 0, 0 };
        using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<MalformedMessageException>(() => MessageCodec.ReadAsync(stream));
    }

    [Fact]
    public void EncodeBody_ThrowsWhenAMessageWouldExceedOneMebibyte()
    {
        var oversized = RejectMessage.Create("t-1", new string('x', ProtocolConstants.MaxPayloadBytes + 1));
        Assert.Throws<MessageTooLargeException>(() => MessageCodec.EncodeBody(oversized));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"v\": 1, \"type\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"v\": 1, \"id\": \"x\" }")]                        // no type
    [InlineData("{ \"v\": 1, \"type\": 7, \"id\": \"x\" }")]           // non-string type
    [InlineData("{ \"v\": 1, \"type\": \"ACCEPT\", \"id\": \"x\" }")]  // no payload
    [InlineData("{ \"v\": 1, \"type\": \"ACCEPT\", \"id\": \"x\", \"payload\": 5 }")]
    public void DecodeBody_RejectsMalformedInput(string json)
    {
        Assert.Throws<MalformedMessageException>(() => MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void DecodeBody_RejectsInvalidUtf8()
    {
        byte[] invalidUtf8 = { 0x7B, 0x22, 0xFF, 0xFE, 0x22, 0x7D };
        Assert.Throws<MalformedMessageException>(() => MessageCodec.DecodeBody(invalidUtf8));
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_HandlesBackToBackFramesOnOneStream()
    {
        using var stream = new MemoryStream();
        await MessageCodec.WriteAsync(stream, HelloMessage.Create("d-1", "PC"));
        await MessageCodec.WriteAsync(stream, OfferMessage.Create("t-1", new[] { new FileEntry { Name = "a", Size = 1 } }));
        await MessageCodec.WriteAsync(stream, DoneMessage.CreateAll("t-1"));
        stream.Position = 0;

        Assert.IsType<HelloMessage>(await MessageCodec.ReadAsync(stream));
        Assert.IsType<OfferMessage>(await MessageCodec.ReadAsync(stream));
        Assert.IsType<DoneMessage>(await MessageCodec.ReadAsync(stream));
        Assert.Null(await MessageCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_ReassemblesAFrameArrivingInSmallChunks()
    {
        // TLS records fragment arbitrarily, so a single Read is never enough.
        byte[] frame = MessageCodec.EncodeFrame(OfferMessage.Create("t-1", new[]
        {
            new FileEntry { Name = "chunky.bin", Size = 999 },
        }));

        using var stream = new DripStream(frame, chunkSize: 1);
        Message? decoded = await MessageCodec.ReadAsync(stream);

        OfferMessage offer = Assert.IsType<OfferMessage>(decoded);
        Assert.Equal("chunky.bin", offer.Payload.Files[0].Name);
    }

    [Fact]
    public void DecodeBody_PreservesUnicodeAndEmojiInFileNames()
    {
        const string name = "假期照片-🌊.jpg";
        Message original = OfferMessage.Create("t-1", new[] { new FileEntry { Name = name, Size = 5 } });
        Message decoded = MessageCodec.DecodeFrame(MessageCodec.EncodeFrame(original));

        Assert.Equal(name, Assert.IsType<OfferMessage>(decoded).Payload.Files[0].Name);
    }

    /// <summary>A stream that hands out at most <c>chunkSize</c> bytes per read.</summary>
    private sealed class DripStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _position;

        public DripStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(Math.Min(_chunkSize, count), _data.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(_data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
