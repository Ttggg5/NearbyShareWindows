using System.Text;
using NearbyShare.Core.Protocol;
using Xunit;

namespace NearbyShare.Core.Tests.Protocol;

/// <summary>
/// Tests the forward-compatibility rule of PROTOCOL.md §4, which explicitly
/// requires both implementations to prove this behaviour.
/// </summary>
public class ForwardCompatibilityTests
{
    private const string ClipboardOffer = """
        { "v": 1, "type": "CLIPBOARD_OFFER", "id": "msg-1", "payload": { "mime": "text/plain" } }
        """;

    [Fact]
    public void DecodingAnUnknownTypeDoesNotThrow()
    {
        Message decoded = MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(ClipboardOffer));

        UnknownMessage unknown = Assert.IsType<UnknownMessage>(decoded);
        Assert.Equal("CLIPBOARD_OFFER", unknown.UnknownType);
        Assert.Equal("msg-1", unknown.Id);
    }

    [Fact]
    public void InspectAnswersUnknownTypeWithUnsupportedTypeAndKeepsTheConnection()
    {
        Message decoded = MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(ClipboardOffer));
        EnvelopeInspection inspection = MessageDispatcher.Inspect(decoded);

        Assert.Equal(EnvelopeDisposition.UnsupportedType, inspection.Disposition);
        Assert.False(inspection.ShouldCloseConnection);

        ErrorMessage error = Assert.IsType<ErrorMessage>(inspection.Response);
        Assert.Equal(ErrorCodes.UnsupportedType, error.Payload.Code);

        // §4: the reply reuses the offending message's id so the peer can correlate it.
        Assert.Equal("msg-1", error.Id);
        Assert.Contains("CLIPBOARD_OFFER", error.Payload.Message);
    }

    [Fact]
    public void InspectAcceptsEveryKnownType()
    {
        Message[] known =
        {
            HelloMessage.Create("d", "n"),
            OfferMessage.Create("t", new[] { new FileEntry { Name = "a", Size = 1 } }),
            AcceptMessage.Create("t"),
            RejectMessage.Create("t", null),
            ProgressMessage.Create("t", 0, 1, 2),
            DoneMessage.CreateAll("t"),
            ErrorMessage.Create("t", ErrorCodes.IoError, "x"),
            CancelMessage.Create("t"),
        };

        foreach (Message message in known)
        {
            EnvelopeInspection inspection = MessageDispatcher.Inspect(message);
            Assert.Equal(EnvelopeDisposition.Accept, inspection.Disposition);
            Assert.Null(inspection.Response);
        }
    }

    [Fact]
    public void InspectRejectsAnUnsupportedVersionAndClosesTheConnection()
    {
        const string futureVersion = """
            { "v": 2, "type": "ACCEPT", "id": "t-1", "payload": { "transferId": "t-1" } }
            """;

        Message decoded = MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(futureVersion));
        EnvelopeInspection inspection = MessageDispatcher.Inspect(decoded);

        Assert.Equal(EnvelopeDisposition.UnsupportedVersion, inspection.Disposition);
        Assert.True(inspection.ShouldCloseConnection);

        ErrorMessage error = Assert.IsType<ErrorMessage>(inspection.Response);
        Assert.Equal(ErrorCodes.UnsupportedVersion, error.Payload.Code);
    }

    [Fact]
    public void VersionIsCheckedBeforeTypeSoAnUnknownTypeOnANewVersionReportsTheVersion()
    {
        const string futureEverything = """
            { "v": 99, "type": "SOMETHING_NEW", "id": "x", "payload": {} }
            """;

        EnvelopeInspection inspection = MessageDispatcher.Inspect(
            MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(futureEverything)));

        // Under an unknown wire format we cannot trust `type` at all, so the
        // version complaint is the correct one.
        Assert.Equal(EnvelopeDisposition.UnsupportedVersion, inspection.Disposition);
    }

    [Fact]
    public async Task ReadDispatchableAsync_RepliesUnsupportedTypeAndContinuesReading()
    {
        // A peer sends an unsupported message, then a message we do understand.
        // §4 requires the connection to remain in a valid state for the second one.
        using var peerToUs = new MemoryStream();
        peerToUs.Write(MessageCodec.EncodeFrame(
            MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(ClipboardOffer))));
        peerToUs.Write(MessageCodec.EncodeFrame(AcceptMessage.Create("t-1")));
        peerToUs.Position = 0;

        var duplex = new DuplexTestStream(peerToUs);

        Message? next = await MessageDispatcher.ReadDispatchableAsync(duplex);

        AcceptMessage accept = Assert.IsType<AcceptMessage>(next);
        Assert.Equal("t-1", accept.Payload.TransferId);

        // Exactly one ERROR{UNSUPPORTED_TYPE} must have gone back to the peer.
        duplex.Written.Position = 0;
        Message? sent = await MessageCodec.ReadAsync(duplex.Written);
        ErrorMessage error = Assert.IsType<ErrorMessage>(sent);
        Assert.Equal(ErrorCodes.UnsupportedType, error.Payload.Code);
        Assert.Null(await MessageCodec.ReadAsync(duplex.Written));
    }

    [Fact]
    public async Task ReadDispatchableAsync_SendsUnsupportedVersionThenThrows()
    {
        using var peerToUs = new MemoryStream();
        peerToUs.Write(MessageCodec.EncodeFrame(
            MessageCodec.DecodeBody(Encoding.UTF8.GetBytes("""
                { "v": 3, "type": "ACCEPT", "id": "t-1", "payload": { "transferId": "t-1" } }
                """))));
        peerToUs.Position = 0;

        var duplex = new DuplexTestStream(peerToUs);

        await Assert.ThrowsAsync<UnsupportedProtocolVersionException>(
            () => MessageDispatcher.ReadDispatchableAsync(duplex));

        duplex.Written.Position = 0;
        ErrorMessage error = Assert.IsType<ErrorMessage>(await MessageCodec.ReadAsync(duplex.Written));
        Assert.Equal(ErrorCodes.UnsupportedVersion, error.Payload.Code);
    }

    [Fact]
    public void UnknownFieldsInAKnownPayloadAreIgnored()
    {
        // PROTOCOL.md §6: additive changes (new optional fields) must not require
        // a version bump, so a receiver has to tolerate fields it does not know.
        const string withExtras = """
            {
              "v": 1,
              "type": "OFFER",
              "id": "t-1",
              "payload": {
                "transferId": "t-1",
                "files": [ { "name": "a.txt", "size": 5, "createdAt": "2026-01-01T00:00:00Z" } ],
                "compression": "zstd"
              }
            }
            """;

        var offer = Assert.IsType<OfferMessage>(MessageCodec.DecodeBody(Encoding.UTF8.GetBytes(withExtras)));
        Assert.Equal("a.txt", offer.Payload.Files[0].Name);
        Assert.Equal(5, offer.Payload.Files[0].Size);
    }

    /// <summary>Reads from one stream and captures everything written to another.</summary>
    private sealed class DuplexTestStream : Stream
    {
        private readonly Stream _input;

        public DuplexTestStream(Stream input)
        {
            _input = input;
        }

        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _input.Length;

        public override long Position { get => _input.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        public override void Flush() => Written.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
