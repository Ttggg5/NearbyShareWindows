using NearbyShare.Core.Networking;
using NearbyShare.Core.Persistence;
using NearbyShare.Core.Services;
using Xunit;

namespace NearbyShare.Core.Tests.Persistence;

/// <summary>Tests for the file-backed settings store and the pinned-peer store.</summary>
public class SettingsAndPinStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nearbyshare-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private string PinsPath => Path.Combine(_directory, "known-peers.json");

    [Fact]
    public async Task SettingsSurviveAcrossRepositoryInstances()
    {
        var first = new JsonDeviceSettingsRepository(SettingsPath);
        await first.SetAsync(SettingKeys.DeviceName, "Living Room PC");

        var second = new JsonDeviceSettingsRepository(SettingsPath);
        Assert.Equal("Living Room PC", await second.GetAsync(SettingKeys.DeviceName));
    }

    [Fact]
    public async Task TheStoreIsGeneralKeyValueNotJustTheDeviceName()
    {
        // Modelled as a general store so future settings need no interface change.
        var repository = new JsonDeviceSettingsRepository(SettingsPath);
        await repository.SetAsync(SettingKeys.DeviceName, "PC");
        await repository.SetAsync(SettingKeys.DownloadFolder, @"C:\Downloads");
        await repository.SetAsync("some.future.setting", "true");

        IReadOnlyDictionary<string, string> all = await repository.GetAllAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal("true", all["some.future.setting"]);
    }

    [Fact]
    public async Task GetReturnsNullForAnUnsetKey()
    {
        var repository = new JsonDeviceSettingsRepository(SettingsPath);
        Assert.Null(await repository.GetAsync("never.set"));
    }

    [Fact]
    public async Task GetOrCreateGeneratesOnceAndThenReturnsTheStoredValue()
    {
        var repository = new JsonDeviceSettingsRepository(SettingsPath);

        string first = await repository.GetOrCreateAsync(SettingKeys.DeviceId, () => Guid.NewGuid().ToString());
        string second = await repository.GetOrCreateAsync(SettingKeys.DeviceId, () => Guid.NewGuid().ToString());

        Assert.Equal(first, second);

        // And across a restart — the device id must stay stable per PROTOCOL.md §1.
        var reopened = new JsonDeviceSettingsRepository(SettingsPath);
        Assert.Equal(first, await reopened.GetOrCreateAsync(SettingKeys.DeviceId, () => Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task SettingAValueToNullRemovesIt()
    {
        var repository = new JsonDeviceSettingsRepository(SettingsPath);
        await repository.SetAsync("k", "v");
        await repository.SetAsync("k", null);

        Assert.Null(await repository.GetAsync("k"));
        Assert.False(await repository.RemoveAsync("k"));
    }

    [Fact]
    public async Task ACorruptSettingsFileFallsBackToDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{ not valid json");

        var repository = new JsonDeviceSettingsRepository(SettingsPath);
        Assert.Null(await repository.GetAsync(SettingKeys.DeviceName));

        await repository.SetAsync(SettingKeys.DeviceName, "Recovered");
        Assert.Equal("Recovered", await repository.GetAsync(SettingKeys.DeviceName));
    }

    [Fact]
    public async Task DeviceIdentityProviderPersistsBothIdAndName()
    {
        var repository = new JsonDeviceSettingsRepository(SettingsPath);
        var provider = new DeviceIdentityProvider(repository, () => "Default Name");

        LocalIdentity identity = await provider.GetIdentityAsync();
        Assert.True(Guid.TryParse(identity.DeviceId, out _));
        Assert.Equal("Default Name", identity.DeviceName);

        await provider.SetDeviceNameAsync("Renamed PC");

        LocalIdentity renamed = await provider.GetIdentityAsync();
        Assert.Equal(identity.DeviceId, renamed.DeviceId);
        Assert.Equal("Renamed PC", renamed.DeviceName);
    }

    // ------------------------------------------------------------------
    // Pinned-peer store
    // ------------------------------------------------------------------

    [Fact]
    public void PinsSurviveAcrossStoreInstances()
    {
        const string deviceId = "3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95";
        string fingerprint = new('a', 64);

        new JsonKnownPeerStore(PinsPath).Pin(deviceId, fingerprint, "Peer");

        var reopened = new JsonKnownPeerStore(PinsPath);
        Assert.Equal(fingerprint, reopened.GetPinnedFingerprint(deviceId));
        Assert.Equal("Peer", Assert.Single(reopened.GetAll()).DeviceName);
    }

    [Fact]
    public void PinRefusesToSilentlyOverwriteADifferentFingerprint()
    {
        const string deviceId = "3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95";
        var store = new JsonKnownPeerStore(PinsPath);
        store.Pin(deviceId, new string('a', 64));

        // PROTOCOL.md §2 step 4.
        Assert.Throws<PeerIdentityChangedException>(() => store.Pin(deviceId, new string('b', 64)));
        Assert.Equal(new string('a', 64), store.GetPinnedFingerprint(deviceId));
    }

    [Fact]
    public void ReplacePinIsTheExplicitOverrideAfterUserConfirmation()
    {
        const string deviceId = "3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95";
        var store = new JsonKnownPeerStore(PinsPath);
        store.Pin(deviceId, new string('a', 64));

        store.ReplacePin(deviceId, new string('b', 64), "Peer");
        Assert.Equal(new string('b', 64), store.GetPinnedFingerprint(deviceId));
    }

    [Fact]
    public void PinsAreNormalizedOnTheWayIn()
    {
        const string deviceId = "3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95";
        var store = new JsonKnownPeerStore(PinsPath);
        store.Pin(deviceId, "AA:BB:CC" + new string('D', 58));

        Assert.Equal("aabbcc" + new string('d', 58), store.GetPinnedFingerprint(deviceId));
    }

    [Fact]
    public void ForgetRemovesAPin()
    {
        const string deviceId = "3b1f8a62-5d47-4c9e-b0a3-7e6c2f1d8b95";
        var store = new JsonKnownPeerStore(PinsPath);
        store.Pin(deviceId, new string('a', 64));

        Assert.True(store.Forget(deviceId));
        Assert.Null(store.GetPinnedFingerprint(deviceId));
        Assert.False(store.Forget(deviceId));
    }

    [Fact]
    public void GetPinnedFingerprintReturnsNullForAnUnknownPeer()
    {
        Assert.Null(new JsonKnownPeerStore(PinsPath).GetPinnedFingerprint("nobody"));
    }

    // ------------------------------------------------------------------
    // History seam
    // ------------------------------------------------------------------

    [Fact]
    public async Task TheNoOpHistoryRepositoryAcceptsWritesAndReturnsNothing()
    {
        ITransferHistoryRepository history = new NoOpTransferHistoryRepository();

        await history.AddAsync(new TransferHistoryEntry(
            "t-1",
            TransferDirection.Outgoing,
            "peer",
            "Peer PC",
            new[] { "a.txt" },
            10,
            10,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TransferOutcome.Completed));

        Assert.Empty(await history.GetRecentAsync());
        await history.ClearAsync();
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
