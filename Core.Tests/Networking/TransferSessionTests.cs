using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Protocol;
using NearbyShare.Core.Services;
using Xunit;

namespace NearbyShare.Core.Tests.Networking;

/// <summary>
/// End-to-end tests of the PROTOCOL.md §5 flow, running a real sender and a real
/// receiver against each other over a loopback TLS connection.
/// </summary>
public class TransferSessionTests : IDisposable
{
    private readonly X509Certificate2 _serverCertificate = CertificateManager.CreateSelfSignedCertificate("Receiver");
    private readonly X509Certificate2 _clientCertificate = CertificateManager.CreateSelfSignedCertificate("Sender");
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "nearbyshare-tests", Guid.NewGuid().ToString("N"));

    private static readonly LocalIdentity SenderIdentity = new("22222222-2222-4222-8222-222222222222", "Sender PC");
    private static readonly LocalIdentity ReceiverIdentity = new("33333333-3333-4333-8333-333333333333", "Receiver PC");

    [Fact]
    public async Task AcceptedTransferMovesFilesByteForByteAndReportsProgress()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");

        string smallPath = WriteFile(sourceFolder, "notes.txt", "hello nearby share"u8.ToArray());
        byte[] largeContent = RandomNumberGenerator.GetBytes(512 * 1024);
        string largePath = WriteFile(sourceFolder, "payload.bin", largeContent);

        var files = new[]
        {
            OutgoingFile.FromPath(smallPath),
            OutgoingFile.FromPath(largePath),
        };

        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));
        var senderProgress = new List<TransferProgress>();
        var receiverProgress = new List<TransferProgress>();

        (bool sent, bool received) = await RunTransferAsync(
            files,
            handler,
            new Progress<TransferProgress>(p => senderProgress.Add(p)),
            new Progress<TransferProgress>(p => receiverProgress.Add(p)));

        Assert.True(sent);
        Assert.True(received);

        Assert.Equal("hello nearby share"u8.ToArray(), File.ReadAllBytes(Path.Combine(destinationFolder, "notes.txt")));
        Assert.Equal(largeContent, File.ReadAllBytes(Path.Combine(destinationFolder, "payload.bin")));

        // No .part files may survive a successful transfer.
        Assert.Empty(Directory.GetFiles(destinationFolder, "*.part"));

        Assert.NotEmpty(senderProgress);
        Assert.NotEmpty(receiverProgress);

        TransferProgress final = receiverProgress[^1];
        Assert.Equal(files.Sum(f => f.Size), final.TotalBytes);
        Assert.Equal(final.TotalBytes, final.TotalBytesTransferred);
        Assert.Equal(1.0, final.OverallFraction, precision: 6);
        Assert.Equal(2, final.FileCount);
    }

    [Fact]
    public async Task RejectedTransferWritesNothingAndReportsRejectionToTheSender()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string path = WriteFile(sourceFolder, "secret.txt", "nope"u8.ToArray());

        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(false));

        (bool sent, bool received) = await RunTransferAsync(new[] { OutgoingFile.FromPath(path) }, handler);

        Assert.False(sent);
        Assert.False(received);
        Assert.Empty(Directory.GetFiles(destinationFolder));
    }

    [Fact]
    public async Task ReceiverSeesTheOfferMetadataBeforeDeciding()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string path = WriteFile(sourceFolder, "holiday.jpg", RandomNumberGenerator.GetBytes(2048));

        IncomingTransferRequest? seen = null;
        var handler = new FolderIncomingTransferHandler(destinationFolder, (request, _) =>
        {
            seen = request;
            return Task.FromResult(true);
        });

        await RunTransferAsync(new[] { OutgoingFile.FromPath(path) }, handler);

        Assert.NotNull(seen);
        Assert.Equal(SenderIdentity.DeviceId, seen!.PeerDeviceId);
        Assert.Equal(SenderIdentity.DeviceName, seen.PeerDeviceName);
        Assert.Equal("holiday.jpg", Assert.Single(seen.Files).Name);
        Assert.Equal(2048, seen.TotalBytes);
        Assert.Equal("image/jpeg", seen.Files[0].Mime);
    }

    [Fact]
    public async Task ChecksumsAreVerifiedWhenTheOfferDeclaresThem()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        byte[] content = RandomNumberGenerator.GetBytes(64 * 1024);
        string path = WriteFile(sourceFolder, "verified.bin", content);

        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var file = OutgoingFile.FromPath(path) with { Sha256 = sha256 };

        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));
        (bool sent, bool received) = await RunTransferAsync(new[] { file }, handler);

        Assert.True(sent);
        Assert.True(received);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(destinationFolder, "verified.bin")));
    }

    [Fact]
    public async Task ADeclaredChecksumThatDoesNotMatchAbortsAndLeavesNoFile()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string path = WriteFile(sourceFolder, "corrupt.bin", RandomNumberGenerator.GetBytes(4096));

        // Declare a checksum the bytes cannot possibly match.
        var file = OutgoingFile.FromPath(path) with { Sha256 = new string('0', 64) };
        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));

        await Assert.ThrowsAnyAsync<Exception>(() => RunTransferAsync(new[] { file }, handler));

        // PROTOCOL.md §5 step 7: a checksum mismatch must not leave the file in place.
        Assert.Empty(Directory.GetFiles(destinationFolder));
    }

    [Fact]
    public async Task AHostileFileNameCannotEscapeTheDestinationFolder()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string escapeTarget = Path.Combine(_workspace, "escaped.txt");
        string path = WriteFile(sourceFolder, "innocent.txt", "payload"u8.ToArray());

        var file = OutgoingFile.FromPath(path) with { Name = @"..\..\escaped.txt" };
        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));

        (bool sent, _) = await RunTransferAsync(new[] { file }, handler);

        Assert.True(sent);
        Assert.False(File.Exists(escapeTarget));
        Assert.Equal("escaped.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(destinationFolder))));
    }

    [Fact]
    public async Task ManyFilesStreamBackToBackWithNoFramingBetweenThem()
    {
        // Regression test for PROTOCOL.md §5 step 4: file bytes go "back-to-back,
        // with no additional framing". Writing any control frame between files
        // (a per-file DONE, or a periodic PROGRESS) would be read back as the next
        // file's content and desynchronize the rest of the stream. With several
        // files of differing sizes, any such leakage corrupts a later file even if
        // the first one happens to look correct.
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");

        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var files = new List<OutgoingFile>();
        int[] sizes = { 1, 0, 17, 65_536, 3, 200_000, 64 * 1024 };

        for (int i = 0; i < sizes.Length; i++)
        {
            byte[] content = RandomNumberGenerator.GetBytes(sizes[i]);
            string name = $"file-{i}.bin";
            expected[name] = content;
            files.Add(OutgoingFile.FromPath(WriteFile(sourceFolder, name, content)));
        }

        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));
        (bool sent, bool received) = await RunTransferAsync(files, handler);

        Assert.True(sent);
        Assert.True(received);

        foreach ((string name, byte[] content) in expected)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(destinationFolder, name)));
        }
    }

    [Fact]
    public async Task AZeroByteFileTransfersSuccessfully()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string path = WriteFile(sourceFolder, "empty.dat", Array.Empty<byte>());

        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));
        (bool sent, bool received) = await RunTransferAsync(new[] { OutgoingFile.FromPath(path) }, handler);

        Assert.True(sent);
        Assert.True(received);
        Assert.Equal(0, new FileInfo(Path.Combine(destinationFolder, "empty.dat")).Length);
    }

    [Fact]
    public async Task ColligingFileNamesAreDeduplicatedRatherThanOverwritten()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        File.WriteAllBytes(Path.Combine(destinationFolder, "report.txt"), "existing"u8.ToArray());

        string path = WriteFile(sourceFolder, "report.txt", "incoming"u8.ToArray());
        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));

        await RunTransferAsync(new[] { OutgoingFile.FromPath(path) }, handler);

        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(Path.Combine(destinationFolder, "report.txt")));
        Assert.Equal("incoming"u8.ToArray(), File.ReadAllBytes(Path.Combine(destinationFolder, "report (2).txt")));
    }

    [Fact]
    public async Task ADeviceIdThatContradictsTheExpectedPeerAbortsTheSession()
    {
        // PROTOCOL.md §2 step 5: the HELLO deviceId must match the peer we thought
        // we dialled, guarding against LAN address reuse or spoofing.
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string path = WriteFile(sourceFolder, "a.txt", "x"u8.ToArray());

        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));

        PeerIdentityMismatchException ex = await Assert.ThrowsAsync<PeerIdentityMismatchException>(
            async () => await RunTransferAsync(
                new[] { OutgoingFile.FromPath(path) },
                handler,
                expectedPeerDeviceId: "99999999-9999-4999-8999-999999999999"));

        Assert.Equal(ReceiverIdentity.DeviceId, ex.ActualDeviceId);
        Assert.Empty(Directory.GetFiles(destinationFolder));
    }

    [Fact]
    public async Task TheSenderStateMachineFollowsTheDocumentedSequence()
    {
        string sourceFolder = CreateFolder("source");
        string destinationFolder = CreateFolder("destination");
        string path = WriteFile(sourceFolder, "a.txt", "x"u8.ToArray());

        var states = new List<TransferState>();
        var handler = new FolderIncomingTransferHandler(destinationFolder, (_, _) => Task.FromResult(true));

        await RunTransferAsync(
            new[] { OutgoingFile.FromPath(path) },
            handler,
            onSenderCreated: session => session.StateChanged += (_, s) => states.Add(s));

        Assert.Equal(
            new[]
            {
                TransferState.Connected,
                TransferState.HelloExchanged,
                TransferState.Offered,
                TransferState.Accepted,
                TransferState.Transferring,
                TransferState.Completed,
            },
            states);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private async Task<(bool Sent, bool Received)> RunTransferAsync(
        IReadOnlyList<OutgoingFile> files,
        IIncomingTransferHandler handler,
        IProgress<TransferProgress>? senderProgress = null,
        IProgress<TransferProgress>? receiverProgress = null,
        string? expectedPeerDeviceId = null,
        Action<TransferSession>? onSenderCreated = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback);
        listener.Start();

        Task<bool> receiveTask = Task.Run(async () =>
        {
            await using TlsConnection? inbound = await listener.AcceptAsync(cts.Token);
            Assert.NotNull(inbound);

            var session = new TransferSession(
                inbound!.Stream,
                ReceiverIdentity,
                new TofuCertificateValidator(new InMemoryKnownPeerStore()),
                inbound.RemoteCertificateFingerprint);

            return await session.ReceiveAsync(handler, receiverProgress, cts.Token);
        }, cts.Token);

        var client = new TlsClient(_clientCertificate, new TofuCertificateValidator(new InMemoryKnownPeerStore()));
        await using TlsConnection outbound = await client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, listener.Port),
            expectedDeviceId: null,
            advertisedFingerprint: null,
            cancellationToken: cts.Token);

        var senderSession = new TransferSession(outbound.Stream, SenderIdentity);
        onSenderCreated?.Invoke(senderSession);

        bool sent;
        try
        {
            sent = await senderSession.SendAsync(files, expectedPeerDeviceId, senderProgress, cts.Token);
        }
        catch
        {
            // Surface the receiver's failure too, so a receiver-side assertion is
            // not swallowed by the sender's exception.
            try
            {
                await receiveTask;
            }
            catch (Exception)
            {
            }

            throw;
        }

        bool received = await receiveTask;
        return (sent, received);
    }

    private string CreateFolder(string name)
    {
        string path = Path.Combine(_workspace, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string folder, string name, byte[] content)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        _serverCertificate.Dispose();
        _clientCertificate.Dispose();

        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
