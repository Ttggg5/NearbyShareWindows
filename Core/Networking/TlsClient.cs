using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NearbyShare.Core.Networking;

/// <summary>
/// Opens outbound connections to peers: plain TCP, then a TLS handshake as the
/// client, with certificate acceptance governed by TOFU pinning
/// (PROTOCOL.md §2 steps 3-4).
/// </summary>
public sealed class TlsClient
{
    /// <summary>SNI value sent during the handshake. Names are not validated under TOFU.</summary>
    public const string TargetHostName = "nearbyshare.local";

    private readonly X509Certificate2 _clientCertificate;
    private readonly TofuCertificateValidator _validator;

    public TlsClient(X509Certificate2 clientCertificate, TofuCertificateValidator validator)
    {
        _clientCertificate = clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// Connects to <paramref name="endPoint"/> and completes the TLS handshake.
    /// </summary>
    /// <param name="endPoint">The peer's IP and port, from its mDNS record or manual entry.</param>
    /// <param name="expectedDeviceId">
    /// The peer's advertised device id, used to look up its pin. Null/empty for a
    /// manually entered address, where the id only becomes known from <c>HELLO</c>.
    /// </param>
    /// <param name="advertisedFingerprint">The peer's advertised TXT <c>fp</c>, when known.</param>
    /// <param name="connectTimeout">How long to wait for the TCP connect. Defaults to 10 seconds.</param>
    /// <exception cref="PeerIdentityChangedException">
    /// The peer's certificate no longer matches its stored pin.
    /// </exception>
    public async Task<TlsConnection> ConnectAsync(
        IPEndPoint endPoint,
        string? expectedDeviceId,
        string? advertisedFingerprint,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        var client = new TcpClient(endPoint.AddressFamily) { NoDelay = true };

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(10));

        try
        {
            await client.ConnectAsync(endPoint.Address, endPoint.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }

        TofuValidationResult? outcome = null;
        var sslStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            _validator.CreateClientCallback(expectedDeviceId, advertisedFingerprint, result => outcome = result));

        try
        {
            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = TargetHostName,

                    // We present our own certificate so the peer can pin us too —
                    // trust is symmetric under PROTOCOL.md §2.
                    ClientCertificates = new X509CertificateCollection { _clientCertificate },
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

                    // TLS 1.2 only. Mutual TLS (a client certificate, which this
                    // protocol always presents) is where TLS 1.3 interop breaks
                    // down: Schannel has a long history of aborting a TLS 1.3
                    // handshake that requests a client certificate against a
                    // non-Windows peer, closing the raw socket instead of sending
                    // an alert -- surfacing here as "Received an unexpected EOF or
                    // 0 bytes from the transport stream" and as nothing at all on
                    // the other side, since the connection never reaches its app
                    // code. TLS 1.2 mutual auth has none of these issues.
                    EnabledSslProtocols = SslProtocols.Tls12,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();

            // Turn the generic AuthenticationException into the specific,
            // user-actionable identity-change error PROTOCOL.md §2 step 4 calls for.
            if (outcome is { IsIdentityChange: true } identityChange)
            {
                throw new PeerIdentityChangedException(
                    identityChange.DeviceId ?? expectedDeviceId ?? "unknown",
                    identityChange.ExpectedFingerprint ?? string.Empty,
                    identityChange.PresentedFingerprint);
            }

            if (outcome is { Outcome: TofuOutcome.AdvertisedFingerprintMismatch } advertisedMismatch)
            {
                throw new PeerIdentityChangedException(
                    advertisedMismatch.DeviceId ?? expectedDeviceId ?? "unknown",
                    advertisedMismatch.ExpectedFingerprint ?? string.Empty,
                    advertisedMismatch.PresentedFingerprint);
            }

            throw new IOException($"TLS handshake with {endPoint} failed: {ex.Message}", ex);
        }

        return new TlsConnection(
            client,
            sslStream,
            endPoint,
            outcome?.PresentedFingerprint,
            outcome);
    }
}
