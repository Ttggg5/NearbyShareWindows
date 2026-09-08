using System.Net;
using NearbyShare.Core.Protocol;

namespace NearbyShare.Core.Discovery;

/// <summary>
/// A peer discovered on the local network, assembled from the DNS-SD SRV/TXT/A
/// records described in PROTOCOL.md §1.
/// </summary>
public sealed record PeerDevice
{
    /// <summary>TXT <c>id</c>: the peer's stable device UUID.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The DNS-SD instance name, i.e. the peer's chosen display name.</summary>
    public required string DeviceName { get; init; }

    /// <summary>TXT <c>fp</c>: lowercase hex SHA-256 of the peer's TLS certificate.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The resolved IP address of the peer.</summary>
    public required IPAddress Address { get; init; }

    /// <summary>TXT <c>port</c> (also carried by the SRV record): the peer's TLS listener port.</summary>
    public required int Port { get; init; }

    /// <summary>TXT <c>os</c>: <c>"android"</c> or <c>"windows"</c>.</summary>
    public string Os { get; init; } = string.Empty;

    /// <summary>TXT <c>v</c>: the peer's protocol version.</summary>
    public int ProtocolVersion { get; init; } = ProtocolConstants.Version;

    /// <summary>When this peer was last seen in an mDNS answer.</summary>
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The fully qualified DNS-SD instance name, used to correlate mDNS records.</summary>
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>True when this peer speaks a protocol version this device understands.</summary>
    public bool IsCompatible => ProtocolVersion == ProtocolConstants.Version;

    /// <summary>The endpoint to open a TCP connection to.</summary>
    public IPEndPoint EndPoint => new(Address, Port);

    /// <summary>
    /// Builds a peer entry for a manually entered IP/port, bypassing mDNS. Used by
    /// the app's manual-connection debug path, where no fingerprint has been
    /// advertised, so TOFU falls back to first-use pinning.
    /// </summary>
    public static PeerDevice Manual(IPAddress address, int port, string? displayName = null) => new()
    {
        // A manual peer has no advertised identity yet; the id is filled in from
        // the HELLO the peer sends after the TLS handshake.
        DeviceId = string.Empty,
        DeviceName = displayName ?? $"{address}:{port}",
        Fingerprint = string.Empty,
        Address = address,
        Port = port,
        Os = string.Empty,
        InstanceName = string.Empty,
    };

    /// <summary>True when this entry came from manual IP entry rather than discovery.</summary>
    public bool IsManual => string.IsNullOrEmpty(InstanceName);
}

/// <summary>How a peer's presence changed.</summary>
public enum PeerChangeKind
{
    /// <summary>A peer became visible for the first time.</summary>
    Added,

    /// <summary>An already-known peer's records changed (address, port, fingerprint…).</summary>
    Updated,

    /// <summary>A peer went away (goodbye packet or expiry).</summary>
    Removed,
}

/// <summary>A single change to the discovered-peer list.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Peer">The peer involved. For <see cref="PeerChangeKind.Removed"/> this is the last known state.</param>
public readonly record struct PeerChange(PeerChangeKind Kind, PeerDevice Peer);
