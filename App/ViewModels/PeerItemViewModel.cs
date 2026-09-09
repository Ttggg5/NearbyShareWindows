using CommunityToolkit.Mvvm.ComponentModel;
using NearbyShare.Core.Discovery;
using NearbyShare.Core.Protocol;

namespace NearbyShare.App.ViewModels;

/// <summary>One row in the discovered-peers list.</summary>
public partial class PeerItemViewModel : ObservableObject
{
    [ObservableProperty]
    private PeerDevice _peer;

    public PeerItemViewModel(PeerDevice peer)
    {
        _peer = peer;
    }

    public string DeviceId => Peer.DeviceId;

    public string DeviceName => Peer.DeviceName;

    public string Address => $"{Peer.Address}:{Peer.Port}";

    /// <summary>A short platform label for the list.</summary>
    public string PlatformLabel => Peer.Os switch
    {
        ProtocolConstants.OsAndroid => "Android",
        ProtocolConstants.OsWindows => "Windows",
        "" => "Unknown platform",
        _ => Peer.Os,
    };

    /// <summary>First eight hex characters of the fingerprint, enough to eyeball a match.</summary>
    public string ShortFingerprint => Peer.Fingerprint.Length >= 8
        ? Peer.Fingerprint[..8]
        : Peer.Fingerprint;

    public bool IsCompatible => Peer.IsCompatible;

    /// <summary>Shown when the peer speaks a protocol version this build does not.</summary>
    public string? IncompatibilityWarning => IsCompatible
        ? null
        : $"This device uses protocol version {Peer.ProtocolVersion}; this app speaks version {ProtocolConstants.Version}.";

    /// <summary>Refreshes the row in place when the peer's mDNS records change.</summary>
    public void UpdateFrom(PeerDevice updated)
    {
        Peer = updated;
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(PlatformLabel));
        OnPropertyChanged(nameof(ShortFingerprint));
        OnPropertyChanged(nameof(IsCompatible));
        OnPropertyChanged(nameof(IncompatibilityWarning));
    }
}
