using System.Security.Cryptography.X509Certificates;
using NearbyShare.Core.Networking;
using Xunit;

namespace NearbyShare.Core.Tests.Networking;

/// <summary>Unit tests for the TOFU decision rules of PROTOCOL.md §2 step 4.</summary>
public class TofuPinningTests
{
    private const string DeviceId = "3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95";

    [Fact]
    public void FirstConnectionToAnUnknownPeerIsTrustedAndPinned()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 peerCertificate = CertificateManager.CreateSelfSignedCertificate("Peer");
        string fingerprint = CertificateManager.ComputeFingerprint(peerCertificate);

        TofuValidationResult result = validator.Validate(DeviceId, fingerprint, peerCertificate);

        Assert.Equal(TofuOutcome.TrustedFirstUse, result.Outcome);
        Assert.True(result.IsTrusted);
        Assert.Equal(fingerprint, store.GetPinnedFingerprint(DeviceId));
    }

    [Fact]
    public void ReconnectingWithTheSameCertificateIsTrustedAgainstThePin()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 peerCertificate = CertificateManager.CreateSelfSignedCertificate("Peer");

        validator.Validate(DeviceId, null, peerCertificate);
        TofuValidationResult second = validator.Validate(DeviceId, null, peerCertificate);

        Assert.Equal(TofuOutcome.TrustedPinned, second.Outcome);
        Assert.Equal(1, store.PinCount);
    }

    [Fact]
    public void AChangedCertificateForAPinnedPeerIsRejectedAndThePinIsNotOverwritten()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 original = CertificateManager.CreateSelfSignedCertificate("Peer");
        using X509Certificate2 impostor = CertificateManager.CreateSelfSignedCertificate("Peer");

        validator.Validate(DeviceId, null, original);
        string originalFingerprint = CertificateManager.ComputeFingerprint(original);

        TofuValidationResult result = validator.Validate(DeviceId, null, impostor);

        Assert.Equal(TofuOutcome.PinnedFingerprintMismatch, result.Outcome);
        Assert.False(result.IsTrusted);
        Assert.True(result.IsIdentityChange);

        // PROTOCOL.md §2 step 4: neither silently proceed nor silently overwrite.
        Assert.Equal(originalFingerprint, store.GetPinnedFingerprint(DeviceId));
        Assert.Equal(CertificateManager.ComputeFingerprint(impostor), result.PresentedFingerprint);
        Assert.Equal(originalFingerprint, result.ExpectedFingerprint);
    }

    [Fact]
    public void ACertificateThatDoesNotMatchTheAdvertisedFingerprintIsRejectedBeforePinning()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 presented = CertificateManager.CreateSelfSignedCertificate("Peer");
        using X509Certificate2 advertised = CertificateManager.CreateSelfSignedCertificate("Other");

        TofuValidationResult result = validator.Validate(
            DeviceId,
            CertificateManager.ComputeFingerprint(advertised),
            presented);

        Assert.Equal(TofuOutcome.AdvertisedFingerprintMismatch, result.Outcome);
        Assert.False(result.IsTrusted);

        // Nothing may be pinned when the advertisement and the certificate disagree.
        Assert.Null(store.GetPinnedFingerprint(DeviceId));
    }

    [Fact]
    public void AMissingCertificateIsRejected()
    {
        var validator = new TofuCertificateValidator(new InMemoryKnownPeerStore());
        TofuValidationResult result = validator.Validate(DeviceId, null, certificate: null);

        Assert.Equal(TofuOutcome.NoCertificate, result.Outcome);
        Assert.False(result.IsTrusted);
    }

    [Fact]
    public void AdvertisedFingerprintComparisonIgnoresCaseAndSeparators()
    {
        var validator = new TofuCertificateValidator(new InMemoryKnownPeerStore());
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate("Peer");

        string uppercaseWithColons = string.Join(
            ":",
            Enumerable.Range(0, 32).Select(i => CertificateManager.ComputeFingerprint(certificate).Substring(i * 2, 2)))
            .ToUpperInvariant();

        TofuValidationResult result = validator.Validate(DeviceId, uppercaseWithColons, certificate);
        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void PinOnFirstUseCanBeSuppressedForAMidHandshakeCheck()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 certificate = CertificateManager.CreateSelfSignedCertificate("Peer");

        TofuValidationResult result = validator.Validate(DeviceId, null, certificate, pinOnFirstUse: false);

        Assert.Equal(TofuOutcome.TrustedFirstUse, result.Outcome);
        Assert.Null(store.GetPinnedFingerprint(DeviceId));
    }

    [Fact]
    public void ValidateFingerprintAppliesTheSameRulesWithoutACertificateObject()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 original = CertificateManager.CreateSelfSignedCertificate("Peer");
        using X509Certificate2 impostor = CertificateManager.CreateSelfSignedCertificate("Peer");

        TofuValidationResult first = validator.ValidateFingerprint(
            DeviceId, null, CertificateManager.ComputeFingerprint(original), "Peer");
        Assert.Equal(TofuOutcome.TrustedFirstUse, first.Outcome);

        TofuValidationResult repeat = validator.ValidateFingerprint(
            DeviceId, null, CertificateManager.ComputeFingerprint(original), "Peer");
        Assert.Equal(TofuOutcome.TrustedPinned, repeat.Outcome);

        TofuValidationResult changed = validator.ValidateFingerprint(
            DeviceId, null, CertificateManager.ComputeFingerprint(impostor), "Peer");
        Assert.Equal(TofuOutcome.PinnedFingerprintMismatch, changed.Outcome);
    }

    [Fact]
    public void ForgettingAPeerMakesTheNextConnectionAFirstUseAgain()
    {
        var store = new InMemoryKnownPeerStore();
        var validator = new TofuCertificateValidator(store);
        using X509Certificate2 original = CertificateManager.CreateSelfSignedCertificate("Peer");
        using X509Certificate2 replacement = CertificateManager.CreateSelfSignedCertificate("Peer");

        validator.Validate(DeviceId, null, original);
        Assert.Equal(TofuOutcome.PinnedFingerprintMismatch, validator.Validate(DeviceId, null, replacement).Outcome);

        // The documented recovery path after a legitimate reinstall.
        Assert.True(store.Forget(DeviceId));
        Assert.Equal(TofuOutcome.TrustedFirstUse, validator.Validate(DeviceId, null, replacement).Outcome);
    }

    [Fact]
    public void DescribeExplainsAnIdentityChangeInUserFacingTerms()
    {
        var validator = new TofuCertificateValidator(new InMemoryKnownPeerStore());
        using X509Certificate2 original = CertificateManager.CreateSelfSignedCertificate("Peer");
        using X509Certificate2 impostor = CertificateManager.CreateSelfSignedCertificate("Peer");

        validator.Validate(DeviceId, null, original);
        string description = validator.Validate(DeviceId, null, impostor).Describe();

        Assert.Contains("identity changed", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aborted", description, StringComparison.OrdinalIgnoreCase);
    }
}
