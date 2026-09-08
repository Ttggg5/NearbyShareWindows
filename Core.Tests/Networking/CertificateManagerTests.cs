using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NearbyShare.Core.Networking;
using Xunit;

namespace NearbyShare.Core.Tests.Networking;

/// <summary>Tests for the fingerprint computation and persistence of PROTOCOL.md §1/§2.</summary>
public class CertificateManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nearbyshare-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ComputeFingerprint_MatchesSha256OfTheDerEncoding()
    {
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate("Test Device");

        string expected = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        string actual = CertificateManager.ComputeFingerprint(certificate);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeFingerprint_IsLowercaseHexWithNoSeparators()
    {
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate("Test Device");
        string fingerprint = CertificateManager.ComputeFingerprint(certificate);

        // PROTOCOL.md §1: "lowercase hex, no separators".
        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f'), $"'{c}' is not lowercase hex."));
    }

    [Fact]
    public void ComputeFingerprint_AgreesWithTheBuiltInThumbprintApi()
    {
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate("Test Device");

        Assert.Equal(
            certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),
            CertificateManager.ComputeFingerprint(certificate));
    }

    [Fact]
    public void ComputeFingerprint_DiffersBetweenDistinctCertificates()
    {
        using X509Certificate2 a = CertificateManager.CreateSelfSignedCertificate("Device A");
        using X509Certificate2 b = CertificateManager.CreateSelfSignedCertificate("Device B");

        Assert.NotEqual(CertificateManager.ComputeFingerprint(a), CertificateManager.ComputeFingerprint(b));
    }

    [Theory]
    [InlineData("AABBCC", "aabbcc")]
    [InlineData("aa:bb:cc", "aabbcc")]
    [InlineData("AA BB CC", "aabbcc")]
    [InlineData("  aabbcc  ", "aabbcc")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeFingerprint_StripsSeparatorsAndCase(string? input, string expected)
    {
        Assert.Equal(expected, CertificateManager.NormalizeFingerprint(input));
    }

    [Fact]
    public void GetOrCreateCertificate_PersistsSoTheFingerprintIsStableAcrossRestarts()
    {
        string path = Path.Combine(_directory, "device-certificate.pfx");

        string firstFingerprint;
        using (var first = new CertificateManager(path))
        {
            firstFingerprint = first.GetFingerprint();
        }

        Assert.True(File.Exists(path));

        // A fresh manager over the same file models an app restart: PROTOCOL.md §2
        // step 1 requires the advertised `fp` not to change.
        using var second = new CertificateManager(path);
        Assert.Equal(firstFingerprint, second.GetFingerprint());
    }

    [Fact]
    public void GetOrCreateCertificate_ReturnsTheSameInstanceWithinOneManager()
    {
        using var manager = new CertificateManager(Path.Combine(_directory, "cert.pfx"));
        Assert.Same(manager.GetOrCreateCertificate(), manager.GetOrCreateCertificate());
    }

    [Fact]
    public void GetOrCreateCertificate_ProducesACertificateWithAPrivateKey()
    {
        using var manager = new CertificateManager(Path.Combine(_directory, "cert.pfx"));
        X509Certificate2 certificate = manager.GetOrCreateCertificate();

        // Without a usable private key SslStream cannot authenticate as a server.
        Assert.True(certificate.HasPrivateKey);
        Assert.True(certificate.NotAfter > DateTime.Now);
        Assert.True(certificate.NotBefore <= DateTime.Now);
    }

    [Fact]
    public void GetOrCreateCertificate_RegeneratesWhenTheStoredFileIsCorrupt()
    {
        string path = Path.Combine(_directory, "cert.pfx");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "this is not a PKCS#12 file");

        using var manager = new CertificateManager(path);
        X509Certificate2 certificate = manager.GetOrCreateCertificate();

        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void CreateSelfSignedCertificate_StripsX500MetacharactersFromTheDeviceName()
    {
        // A device name is user input and flows into the certificate subject; it
        // must not be able to inject additional RDNs.
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate("Evil,O=Acme");

        Assert.Contains("Evil", certificate.Subject);
        Assert.DoesNotContain("O=Acme", certificate.Subject);
        Assert.Single(new X500DistinguishedName(certificate.SubjectName.RawData).EnumerateRelativeDistinguishedNames());
    }

    [Theory]
    [InlineData("Desk = Home")]
    [InlineData("PC #1")]
    [InlineData(@"Back\slash")]
    [InlineData("Quote\"Name")]
    [InlineData("")]
    public void CreateSelfSignedCertificate_AcceptsAnyDeviceNameWithoutThrowing(string deviceName)
    {
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate(deviceName);
        Assert.True(certificate.HasPrivateKey);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
