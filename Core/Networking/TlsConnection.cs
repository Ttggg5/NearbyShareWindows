using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace NearbyShare.Core.Networking;

/// <summary>
/// An established TLS connection to a peer: the authenticated
/// <see cref="SslStream"/> plus the identity facts gathered during the handshake.
/// </summary>
public sealed class TlsConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private bool _disposed;

    internal TlsConnection(
        TcpClient client,
        SslStream stream,
        IPEndPoint? remoteEndPoint,
        string? remoteFingerprint,
        TofuValidationResult? validation)
    {
        _client = client;
        _stream = stream;
        RemoteEndPoint = remoteEndPoint;
        RemoteCertificateFingerprint = remoteFingerprint;
        Validation = validation;
    }

    /// <summary>The authenticated stream to read control messages and file bytes from.</summary>
    public Stream Stream => _stream;

    /// <summary>The peer's address, if the socket still knows it.</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// SHA-256 fingerprint of the certificate the peer presented, or null if it
    /// presented none (only possible for an inbound connection).
    /// </summary>
    public string? RemoteCertificateFingerprint { get; }

    /// <summary>
    /// The TOFU result from the handshake. Null for inbound connections, where the
    /// peer's identity is not known until <c>HELLO</c> (PROTOCOL.md §2 step 5).
    /// </summary>
    public TofuValidationResult? Validation { get; internal set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposing a stream whose socket is already gone throws; nothing to do.
        }

        _client.Dispose();
    }
}
