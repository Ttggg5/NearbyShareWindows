using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NearbyShare.Core.Networking;

/// <summary>
/// Accepts inbound peer connections and completes the TLS handshake as the
/// server side (PROTOCOL.md §2 step 3).
/// </summary>
/// <remarks>
/// The client certificate is requested but not validated at the TLS layer: the
/// peer's device id is unknown until it sends <c>HELLO</c>, so its fingerprint is
/// merely recorded here and the TOFU check runs afterwards, in
/// <see cref="TransferSession"/>.
/// </remarks>
public sealed class TlsListener : IDisposable, IAsyncDisposable
{
    private readonly X509Certificate2 _serverCertificate;
    private readonly IPAddress _bindAddress;
    private readonly int _requestedPort;

    private TcpListener? _listener;
    private bool _disposed;

    /// <param name="serverCertificate">This device's long-lived certificate.</param>
    /// <param name="bindAddress">Address to bind. Defaults to all interfaces.</param>
    /// <param name="port">Port to bind, or 0 to let the OS choose an ephemeral port.</param>
    public TlsListener(X509Certificate2 serverCertificate, IPAddress? bindAddress = null, int port = 0)
    {
        _serverCertificate = serverCertificate ?? throw new ArgumentNullException(nameof(serverCertificate));
        _bindAddress = bindAddress ?? IPAddress.Any;
        _requestedPort = port;
    }

    /// <summary>The port actually bound. Only meaningful after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>True while the listener socket is bound.</summary>
    public bool IsListening => _listener is not null;

    /// <summary>
    /// Binds the socket and starts listening. The bound <see cref="Port"/> must be
    /// read after this call, because port 0 asks the OS to pick one — and that
    /// port is what gets advertised in the mDNS TXT record.
    /// </summary>
    public void Start(int backlog = 16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(_bindAddress, _requestedPort);
        listener.Start(backlog);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Accepts one connection and completes the TLS handshake.
    /// </summary>
    /// <returns>The established connection, or null if the listener was stopped.</returns>
    public async Task<TlsConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("Start() must be called before accepting connections.");

        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        string? clientFingerprint = null;
        var sslStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            TofuCertificateValidator.CreateServerCallback(fingerprint => clientFingerprint = fingerprint));

        try
        {
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCertificate,

                    // Ask for the peer's certificate so we can pin it, but do not
                    // let the TLS layer decide: PROTOCOL.md §2 step 4 replaces
                    // chain validation with fingerprint pinning after HELLO.
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

                    // TLS 1.2 only -- see the matching comment in TlsClient. Mutual
                    // TLS 1.3 (client certificates) has known Schannel interop
                    // failures against non-Windows peers; TLS 1.2 mutual auth does
                    // not have this problem.
                    EnabledSslProtocols = SslProtocols.Tls12,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            throw;
        }

        return new TlsConnection(client, sslStream, client.Client.RemoteEndPoint as IPEndPoint, clientFingerprint, validation: null);
    }

    /// <summary>
    /// Runs an accept loop until cancelled, invoking <paramref name="handler"/> for
    /// each connection on a background task so one slow transfer never blocks the
    /// next inbound offer. Handler exceptions are surfaced through
    /// <paramref name="onError"/> rather than tearing the loop down.
    /// </summary>
    public async Task RunAsync(
        Func<TlsConnection, CancellationToken, Task> handler,
        Action<Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (_listener is null)
        {
            Start();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TlsConnection? connection;
            try
            {
                connection = await AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed handshake (peer aborted, TLS mismatch) must not stop us
                // listening for the next peer.
                onError?.Invoke(ex);
                continue;
            }

            if (connection is null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }
                finally
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Stops listening. In-flight connections are unaffected.</summary>
    public void Stop()
    {
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
