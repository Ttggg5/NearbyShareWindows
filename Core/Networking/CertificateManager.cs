using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NearbyShare.Core.Networking;

/// <summary>
/// Owns this device's single long-lived self-signed TLS certificate
/// (PROTOCOL.md §2 step 1). The certificate is generated once and persisted, so
/// the SHA-256 fingerprint advertised as the mDNS TXT <c>fp</c> field stays
/// stable across app restarts and peers' TOFU pins keep matching.
/// </summary>
public sealed class CertificateManager : IDisposable
{
    /// <summary>Default lifetime of the generated certificate.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(365 * 10);

    private readonly string _certificatePath;
    private readonly string _subjectCommonName;
    private readonly TimeSpan _lifetime;
    private readonly object _gate = new();

    private X509Certificate2? _certificate;

    /// <param name="certificatePath">Path of the PKCS#12 file holding the certificate and its private key.</param>
    /// <param name="subjectCommonName">CN placed in the certificate subject; purely cosmetic under TOFU.</param>
    /// <param name="lifetime">Certificate validity period. Defaults to ten years.</param>
    public CertificateManager(string certificatePath, string subjectCommonName = "NearbyShare Device", TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);

        _certificatePath = certificatePath;
        _subjectCommonName = string.IsNullOrWhiteSpace(subjectCommonName) ? "NearbyShare Device" : subjectCommonName;
        _lifetime = lifetime ?? DefaultLifetime;
    }

    /// <summary>
    /// Returns the device certificate, generating and persisting one on first
    /// call. A stored certificate that is expired or unreadable is replaced —
    /// which changes this device's fingerprint, so peers will see the identity
    /// change described in PROTOCOL.md §2 step 4.
    /// </summary>
    public X509Certificate2 GetOrCreateCertificate()
    {
        lock (_gate)
        {
            if (_certificate is not null)
            {
                return _certificate;
            }

            if (TryLoadExisting(out X509Certificate2? loaded))
            {
                _certificate = loaded;
                return _certificate!;
            }

            _certificate = CreateAndPersist();
            return _certificate;
        }
    }

    /// <summary>The lowercase hex SHA-256 fingerprint advertised as the TXT <c>fp</c> field.</summary>
    public string GetFingerprint() => ComputeFingerprint(GetOrCreateCertificate());

    /// <summary>
    /// Computes the PROTOCOL.md §1/§2 fingerprint: SHA-256 over the certificate's
    /// DER encoding, lowercase hex, no separators.
    /// </summary>
    public static string ComputeFingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return ComputeFingerprint(certificate.GetRawCertData());
    }

    /// <summary>Computes the fingerprint of a raw DER-encoded certificate.</summary>
    public static string ComputeFingerprint(ReadOnlySpan<byte> derEncodedCertificate)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(derEncodedCertificate, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a fingerprint for comparison: trimmed, lowercased, and with the
    /// colon/space separators some tooling emits removed. Fingerprints must be
    /// compared with this, never with a raw string equality on user/wire input.
    /// </summary>
    public static string NormalizeFingerprint(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint)
            ? string.Empty
            : fingerprint.Replace(":", string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Generates a fresh self-signed certificate with a private key. Exposed for
    /// tests, which need throwaway identities for both ends of a loopback
    /// handshake.
    /// </summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string commonName, TimeSpan? lifetime = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={EscapeCommonName(commonName)}"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1"), // serverAuth — we listen for inbound transfers.
                new Oid("1.3.6.1.5.5.7.3.2"), // clientAuth — we also present this cert when connecting.
            },
            critical: false));

        // A SAN is required for SslStream to consider the certificate usable at
        // all on some platforms, even though TOFU ignores name validation.
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("nearbyshare.local");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.Add(lifetime ?? DefaultLifetime);

        using X509Certificate2 generated = request.CreateSelfSigned(notBefore, notAfter);

        // Round-tripping through PKCS#12 attaches the private key in the form
        // SslStream requires for server authentication on Windows; a certificate
        // straight out of CreateSelfSigned can carry an ephemeral key handle that
        // SChannel refuses.
        byte[] pkcs12 = generated.Export(X509ContentType.Pkcs12);
        try
        {
            return LoadPkcs12(pkcs12);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }

    private bool TryLoadExisting(out X509Certificate2? certificate)
    {
        certificate = null;
        if (!File.Exists(_certificatePath))
        {
            return false;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(_certificatePath);
            X509Certificate2 loaded = LoadPkcs12(bytes);

            if (!loaded.HasPrivateKey || loaded.NotAfter <= DateTime.Now)
            {
                loaded.Dispose();
                return false;
            }

            certificate = loaded;
            return true;
        }
        catch (CryptographicException)
        {
            // Corrupt or unreadable store: fall through and regenerate.
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private X509Certificate2 CreateAndPersist()
    {
        X509Certificate2 certificate = CreateSelfSignedCertificate(_subjectCommonName, _lifetime);

        string? directory = Path.GetDirectoryName(_certificatePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            // Write to a temp file then move, so a crash mid-write cannot leave a
            // truncated store that would silently change our fingerprint.
            string temporaryPath = _certificatePath + ".tmp";
            File.WriteAllBytes(temporaryPath, pkcs12);
            RestrictToCurrentUser(temporaryPath);
            File.Move(temporaryPath, _certificatePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }

        return certificate;
    }

    private static X509Certificate2 LoadPkcs12(byte[] pkcs12)
    {
#pragma warning disable SYSLIB0057 // X509Certificate2 ctor is obsolete on .NET 9+; kept for net8.0 compatibility.
        // EphemeralKeySet is deliberately NOT used: SChannel cannot use an
        // ephemeral key for server-side TLS on Windows.
        return new X509Certificate2(
            pkcs12,
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
    }

    /// <summary>
    /// Tightens file permissions on the private-key store. On Windows the file
    /// inherits the user's %LOCALAPPDATA% ACL; on Unix we set 0600 explicitly.
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort; not all filesystems support mode bits.
        }
    }

    private static string EscapeCommonName(string commonName)
    {
        string trimmed = string.IsNullOrWhiteSpace(commonName) ? "NearbyShare Device" : commonName.Trim();

        // X.500 metacharacters would otherwise let a device name inject extra RDNs.
        foreach (char c in new[] { '\\', ',', '+', '"', '<', '>', ';', '=' })
        {
            trimmed = trimmed.Replace(c.ToString(), "\\" + c);
        }

        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }
}
