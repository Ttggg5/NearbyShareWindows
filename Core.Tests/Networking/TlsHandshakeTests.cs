using System.Net;
using System.Security.Cryptography.X509Certificates;
using NearbyShare.Core.Networking;
using NearbyShare.Core.Protocol;
using Xunit;

namespace NearbyShare.Core.Tests.Networking;

/// <summary>
/// Exercises <see cref="TlsListener"/> and <see cref="TlsClient"/> against each
/// other over a real loopback socket, so the TOFU
/// <c>RemoteCertificateValidationCallback</c> of PROTOCOL.md §2 runs inside an
/// actual <c>SslStream</c> handshake rather than being tested in isolation.
/// </summary>
public class TlsHandshakeTests : IDisposable
{
    private const string ServerDeviceId = "11111111-1111-4111-8111-111111111111";

    private readonly X509Certificate2 _serverCertificate = CertificateManager.CreateSelfSignedCertificate("Server Device");
    private readonly X509Certificate2 _clientCertificate = CertificateManager.CreateSelfSignedCertificate("Client Device");

    [Fact]
    public async Task HandshakeSucceedsAndPinsTheServerOnFirstUse()
    {
        var store = new InMemoryKnownPeerStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback);
        listener.Start();

        Task<TlsConnection?> acceptTask = listener.AcceptAsync(cts.Token);

        var client = new TlsClient(_clientCertificate, new TofuCertificateValidator(store));
        await using TlsConnection outbound = await client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, listener.Port),
            ServerDeviceId,
            advertisedFingerprint: null,
            cancellationToken: cts.Token);

        await using TlsConnection? inbound = await acceptTask;
        Assert.NotNull(inbound);

        string serverFingerprint = CertificateManager.ComputeFingerprint(_serverCertificate);
        Assert.Equal(serverFingerprint, outbound.RemoteCertificateFingerprint);
        Assert.Equal(TofuOutcome.TrustedFirstUse, outbound.Validation!.Value.Outcome);
        Assert.Equal(serverFingerprint, store.GetPinnedFingerprint(ServerDeviceId));

        // The server side records the client's certificate too, so it can pin the
        // client once HELLO reveals its device id.
        Assert.Equal(CertificateManager.ComputeFingerprint(_clientCertificate), inbound!.RemoteCertificateFingerprint);
    }

    [Fact]
    public async Task AControlMessageSurvivesTheEncryptedRoundTrip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback);
        listener.Start();

        Task<TlsConnection?> acceptTask = listener.AcceptAsync(cts.Token);

        var client = new TlsClient(_clientCertificate, new TofuCertificateValidator(new InMemoryKnownPeerStore()));
        await using TlsConnection outbound = await client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, listener.Port), ServerDeviceId, null, cancellationToken: cts.Token);

        await using TlsConnection? inbound = await acceptTask;

        await MessageCodec.WriteAsync(outbound.Stream, HelloMessage.Create("d-1", "Client"), cts.Token);
        Message? received = await MessageCodec.ReadAsync(inbound!.Stream, cts.Token);

        HelloMessage hello = Assert.IsType<HelloMessage>(received);
        Assert.Equal("d-1", hello.Payload.DeviceId);
        Assert.Equal("Client", hello.Payload.DeviceName);
    }

    [Fact]
    public async Task HandshakeIsRefusedWhenThePinnedFingerprintNoLongerMatches()
    {
        var store = new InMemoryKnownPeerStore();

        // The server presents a different certificate than the one pinned earlier,
        // as if the app had been reinstalled — or something were impersonating it.
        using X509Certificate2 previousIdentity = CertificateManager.CreateSelfSignedCertificate("Server Device");
        store.Pin(ServerDeviceId, CertificateManager.ComputeFingerprint(previousIdentity));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback);
        listener.Start();

        // The server's accept will fail too when the client aborts the handshake.
        Task acceptTask = Task.Run(async () =>
        {
            try
            {
                TlsConnection? connection = await listener.AcceptAsync(cts.Token);
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
            catch (Exception)
            {
                // Expected: the client rejects our certificate mid-handshake.
            }
        });

        var client = new TlsClient(_clientCertificate, new TofuCertificateValidator(store));

        PeerIdentityChangedException ex = await Assert.ThrowsAsync<PeerIdentityChangedException>(
            () => client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, listener.Port),
                ServerDeviceId,
                advertisedFingerprint: null,
                cancellationToken: cts.Token));

        Assert.Equal(ServerDeviceId, ex.DeviceId);
        Assert.Equal(CertificateManager.ComputeFingerprint(previousIdentity), ex.ExpectedFingerprint);
        Assert.Equal(CertificateManager.ComputeFingerprint(_serverCertificate), ex.PresentedFingerprint);
        Assert.Equal(ErrorCodes.IdentityMismatch, ex.ErrorCode);

        // The stored pin must be untouched.
        Assert.Equal(CertificateManager.ComputeFingerprint(previousIdentity), store.GetPinnedFingerprint(ServerDeviceId));

        await acceptTask;
    }

    [Fact]
    public async Task HandshakeIsRefusedWhenTheCertificateContradictsTheAdvertisedFingerprint()
    {
        var store = new InMemoryKnownPeerStore();
        using X509Certificate2 unrelated = CertificateManager.CreateSelfSignedCertificate("Someone Else");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback);
        listener.Start();

        Task acceptTask = Task.Run(async () =>
        {
            try
            {
                TlsConnection? connection = await listener.AcceptAsync(cts.Token);
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
            catch (Exception)
            {
            }
        });

        var client = new TlsClient(_clientCertificate, new TofuCertificateValidator(store));

        await Assert.ThrowsAsync<PeerIdentityChangedException>(
            () => client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, listener.Port),
                ServerDeviceId,
                advertisedFingerprint: CertificateManager.ComputeFingerprint(unrelated),
                cancellationToken: cts.Token));

        Assert.Null(store.GetPinnedFingerprint(ServerDeviceId));
        await acceptTask;
    }

    [Fact]
    public async Task HandshakeSucceedsWhenTheAdvertisedFingerprintMatches()
    {
        var store = new InMemoryKnownPeerStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback);
        listener.Start();
        Task<TlsConnection?> acceptTask = listener.AcceptAsync(cts.Token);

        var client = new TlsClient(_clientCertificate, new TofuCertificateValidator(store));
        await using TlsConnection outbound = await client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, listener.Port),
            ServerDeviceId,
            CertificateManager.ComputeFingerprint(_serverCertificate),
            cancellationToken: cts.Token);

        await using TlsConnection? inbound = await acceptTask;

        Assert.True(outbound.Validation!.Value.IsTrusted);
        Assert.NotNull(inbound);
    }

    [Fact]
    public void ListenerBindsAnEphemeralPortWhenAskedForPortZero()
    {
        // The bound port is what gets advertised in the mDNS TXT record, so it
        // must be readable after Start().
        using var listener = new TlsListener(_serverCertificate, IPAddress.Loopback, port: 0);
        listener.Start();

        Assert.InRange(listener.Port, 1, 65535);
        Assert.True(listener.IsListening);

        listener.Stop();
        Assert.False(listener.IsListening);
    }

    public void Dispose()
    {
        _serverCertificate.Dispose();
        _clientCertificate.Dispose();
        GC.SuppressFinalize(this);
    }
}
