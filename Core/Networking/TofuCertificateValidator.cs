using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using NearbyShare.Core.Protocol;

namespace NearbyShare.Core.Networking;

/// <summary>The result of applying the PROTOCOL.md §2 step 4 TOFU rules.</summary>
public enum TofuOutcome
{
    /// <summary>Peer was unknown; its fingerprint has now been pinned.</summary>
    TrustedFirstUse,

    /// <summary>Peer's certificate matches the fingerprint pinned previously.</summary>
    TrustedPinned,

    /// <summary>
    /// A different fingerprint is pinned for this device id. A security event
    /// comparable to an SSH host-key change: abort and warn the user.
    /// </summary>
    PinnedFingerprintMismatch,

    /// <summary>The presented certificate does not match the fingerprint advertised over mDNS.</summary>
    AdvertisedFingerprintMismatch,

    /// <summary>The peer presented no certificate at all.</summary>
    NoCertificate,
}

/// <summary>The outcome of a TOFU check, plus the details needed to explain it to the user.</summary>
/// <param name="Outcome">What the validator decided.</param>
/// <param name="DeviceId">The peer device id the check was made against, if known.</param>
/// <param name="PresentedFingerprint">The fingerprint of the certificate the peer actually presented.</param>
/// <param name="ExpectedFingerprint">The pinned or advertised fingerprint that was expected, if any.</param>
public readonly record struct TofuValidationResult(
    TofuOutcome Outcome,
    string? DeviceId,
    string PresentedFingerprint,
    string? ExpectedFingerprint)
{
    /// <summary>True when the connection may proceed.</summary>
    public bool IsTrusted => Outcome is TofuOutcome.TrustedFirstUse or TofuOutcome.TrustedPinned;

    /// <summary>
    /// True when the failure is an identity change rather than a plain
    /// mismatch — the case that warrants a prominent warning rather than a
    /// generic connection error.
    /// </summary>
    public bool IsIdentityChange => Outcome == TofuOutcome.PinnedFingerprintMismatch;

    /// <summary>A user-facing explanation of the outcome.</summary>
    public string Describe() => Outcome switch
    {
        TofuOutcome.TrustedFirstUse => "First connection to this device; its identity has been remembered.",
        TofuOutcome.TrustedPinned => "This device's identity matches the one remembered previously.",
        TofuOutcome.PinnedFingerprintMismatch =>
            $"This device's identity changed. Expected certificate {ExpectedFingerprint}, but it presented {PresentedFingerprint}. " +
            "Either the app was reinstalled on that device, or something is impersonating it. The transfer was aborted.",
        TofuOutcome.AdvertisedFingerprintMismatch =>
            $"The device presented certificate {PresentedFingerprint}, which does not match the fingerprint {ExpectedFingerprint} it advertised on the network.",
        TofuOutcome.NoCertificate => "The device did not present a TLS certificate.",
        _ => "Unknown validation outcome.",
    };
}

/// <summary>Raised when a peer's certificate fingerprint no longer matches its stored pin.</summary>
public sealed class PeerIdentityChangedException : ProtocolException
{
    public PeerIdentityChangedException(string deviceId, string expectedFingerprint, string presentedFingerprint)
        : base($"Device '{deviceId}' presented certificate {presentedFingerprint} but {expectedFingerprint} was pinned previously. " +
               "Refusing to connect (PROTOCOL.md §2 step 4).",
            ErrorCodes.IdentityMismatch)
    {
        DeviceId = deviceId;
        ExpectedFingerprint = expectedFingerprint;
        PresentedFingerprint = presentedFingerprint;
    }

    public string DeviceId { get; }

    public string ExpectedFingerprint { get; }

    public string PresentedFingerprint { get; }
}

/// <summary>
/// Implements the trust-on-first-use certificate pinning of PROTOCOL.md §2
/// step 4. Chain-of-trust validation is deliberately not performed — both sides
/// use self-signed certificates and there is no CA.
/// </summary>
public sealed class TofuCertificateValidator
{
    private readonly IKnownPeerStore _knownPeers;

    public TofuCertificateValidator(IKnownPeerStore knownPeers)
    {
        _knownPeers = knownPeers ?? throw new ArgumentNullException(nameof(knownPeers));
    }

    /// <summary>
    /// Applies the TOFU rules to a presented certificate.
    /// </summary>
    /// <param name="deviceId">
    /// The peer's device id. May be null/empty when the identity is not yet known
    /// (an inbound connection, before <c>HELLO</c>), in which case only the
    /// advertised-fingerprint check applies and nothing is pinned.
    /// </param>
    /// <param name="advertisedFingerprint">
    /// The <c>fp</c> value advertised over mDNS for this peer, when the connection
    /// was initiated from a discovered peer. Null for manual connections.
    /// </param>
    /// <param name="certificate">The certificate the peer presented.</param>
    /// <param name="pinOnFirstUse">
    /// When false the check is evaluated but nothing is written to the store —
    /// used to validate mid-handshake, before <c>HELLO</c> has confirmed the id.
    /// </param>
    public TofuValidationResult Validate(
        string? deviceId,
        string? advertisedFingerprint,
        X509Certificate? certificate,
        bool pinOnFirstUse = true)
    {
        if (certificate is null)
        {
            return new TofuValidationResult(TofuOutcome.NoCertificate, deviceId, string.Empty, advertisedFingerprint);
        }

        string presented = CertificateManager.ComputeFingerprint(certificate);
        string? advertised = CertificateManager.NormalizeFingerprint(advertisedFingerprint);
        if (advertised.Length == 0)
        {
            advertised = null;
        }

        // Step 4 bullet 2: the presented certificate must match what mDNS
        // advertised for this peer, when we have an advertisement to compare to.
        if (advertised is not null && !FingerprintsEqual(advertised, presented))
        {
            return new TofuValidationResult(TofuOutcome.AdvertisedFingerprintMismatch, deviceId, presented, advertised);
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            // Identity not established yet: accept for now. The caller re-runs this
            // check with the id from HELLO before any file data moves.
            return new TofuValidationResult(TofuOutcome.TrustedFirstUse, deviceId, presented, advertised);
        }

        string? pinned = _knownPeers.GetPinnedFingerprint(deviceId);

        // Step 4 bullet 4: a stored pin that does not match is a security event.
        // Do not overwrite it, do not proceed.
        if (pinned is not null && !FingerprintsEqual(pinned, presented))
        {
            return new TofuValidationResult(TofuOutcome.PinnedFingerprintMismatch, deviceId, presented, pinned);
        }

        if (pinned is not null)
        {
            return new TofuValidationResult(TofuOutcome.TrustedPinned, deviceId, presented, pinned);
        }

        // Step 4 bullet 3: first contact with this id — remember the fingerprint.
        if (pinOnFirstUse)
        {
            _knownPeers.Pin(deviceId, presented);
        }

        return new TofuValidationResult(TofuOutcome.TrustedFirstUse, deviceId, presented, null);
    }

    /// <summary>
    /// The same TOFU rules as <see cref="Validate"/>, but starting from a
    /// fingerprint rather than a certificate object. Used for inbound connections,
    /// where the certificate is consumed during the handshake and only its
    /// fingerprint is retained until <c>HELLO</c> reveals the peer's id
    /// (PROTOCOL.md §2 step 5).
    /// </summary>
    public TofuValidationResult ValidateFingerprint(
        string? deviceId,
        string? advertisedFingerprint,
        string? presentedFingerprint,
        string? deviceName = null,
        bool pinOnFirstUse = true)
    {
        string presented = CertificateManager.NormalizeFingerprint(presentedFingerprint);
        if (presented.Length == 0)
        {
            return new TofuValidationResult(TofuOutcome.NoCertificate, deviceId, string.Empty, advertisedFingerprint);
        }

        string? advertised = CertificateManager.NormalizeFingerprint(advertisedFingerprint);
        if (advertised.Length == 0)
        {
            advertised = null;
        }

        if (advertised is not null && !string.Equals(advertised, presented, StringComparison.Ordinal))
        {
            return new TofuValidationResult(TofuOutcome.AdvertisedFingerprintMismatch, deviceId, presented, advertised);
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return new TofuValidationResult(TofuOutcome.TrustedFirstUse, deviceId, presented, advertised);
        }

        string? pinned = CertificateManager.NormalizeFingerprint(_knownPeers.GetPinnedFingerprint(deviceId));
        if (pinned.Length == 0)
        {
            if (pinOnFirstUse)
            {
                _knownPeers.Pin(deviceId, presented, deviceName);
            }

            return new TofuValidationResult(TofuOutcome.TrustedFirstUse, deviceId, presented, null);
        }

        return string.Equals(pinned, presented, StringComparison.Ordinal)
            ? new TofuValidationResult(TofuOutcome.TrustedPinned, deviceId, presented, pinned)
            : new TofuValidationResult(TofuOutcome.PinnedFingerprintMismatch, deviceId, presented, pinned);
    }

    /// <summary>
    /// Builds the <see cref="RemoteCertificateValidationCallback"/> used when this
    /// device is the TLS <em>client</em> and already knows which peer it dialled.
    /// </summary>
    /// <param name="deviceId">The expected peer device id.</param>
    /// <param name="advertisedFingerprint">The peer's advertised <c>fp</c>, if discovered via mDNS.</param>
    /// <param name="onResult">Receives the outcome, so the caller can report why a handshake failed.</param>
    public RemoteCertificateValidationCallback CreateClientCallback(
        string? deviceId,
        string? advertisedFingerprint,
        Action<TofuValidationResult> onResult)
    {
        ArgumentNullException.ThrowIfNull(onResult);

        return (_, certificate, _, _) =>
        {
            // sslPolicyErrors is intentionally ignored: RemoteCertificateChainErrors
            // and NameMismatch are expected for self-signed peer certificates, and
            // PROTOCOL.md §2 step 4 replaces chain validation with pinning.
            TofuValidationResult result = Validate(deviceId, advertisedFingerprint, certificate);
            onResult(result);
            return result.IsTrusted;
        };
    }

    /// <summary>
    /// Builds the callback used when this device is the TLS <em>server</em>. The
    /// client's identity is unknown until <c>HELLO</c> arrives, so the handshake
    /// only records the fingerprint; <see cref="Validate"/> is re-run with the
    /// device id from <c>HELLO</c> before the transfer proceeds (PROTOCOL.md §2
    /// step 5).
    /// </summary>
    public static RemoteCertificateValidationCallback CreateServerCallback(Action<string?> onClientFingerprint)
    {
        ArgumentNullException.ThrowIfNull(onClientFingerprint);

        return (_, certificate, _, _) =>
        {
            onClientFingerprint(certificate is null ? null : CertificateManager.ComputeFingerprint(certificate));

            // Accept any client certificate at the TLS layer, including none: a
            // peer that fails the post-HELLO TOFU check is rejected there, with a
            // protocol-level ERROR the peer can display.
            return true;
        };
    }

    private static bool FingerprintsEqual(string a, string b) =>
        string.Equals(
            CertificateManager.NormalizeFingerprint(a),
            CertificateManager.NormalizeFingerprint(b),
            StringComparison.Ordinal);
}
