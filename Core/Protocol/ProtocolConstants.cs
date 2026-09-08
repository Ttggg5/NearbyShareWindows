namespace NearbyShare.Core.Protocol;

/// <summary>
/// Constants defined by PROTOCOL.md. Values here are part of the cross-implementation
/// wire contract with the Android app — do not change without updating PROTOCOL.md.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Envelope <c>v</c> value emitted by this implementation (PROTOCOL.md §4).</summary>
    public const int Version = 1;

    /// <summary>DNS-SD service type (PROTOCOL.md §1).</summary>
    public const string ServiceType = "_nearbyshare._tcp";

    /// <summary>Fully qualified DNS-SD service name including the local domain.</summary>
    public const string ServiceTypeFullyQualified = "_nearbyshare._tcp.local.";

    /// <summary>Maximum control-message payload size in bytes (PROTOCOL.md §3): 1 MiB.</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Length of the big-endian uint32 frame length prefix (PROTOCOL.md §3).</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>Platform identifier advertised in the mDNS TXT <c>os</c> field (PROTOCOL.md §1).</summary>
    public const string OsWindows = "windows";

    /// <summary>Platform identifier used by the Android counterpart.</summary>
    public const string OsAndroid = "android";
}

/// <summary>
/// The <c>type</c> discriminator strings for the MVP message set (PROTOCOL.md §5).
/// </summary>
public static class MessageTypes
{
    public const string Hello = "HELLO";
    public const string Offer = "OFFER";
    public const string Accept = "ACCEPT";
    public const string Reject = "REJECT";
    public const string Progress = "PROGRESS";
    public const string Done = "DONE";
    public const string Error = "ERROR";
    public const string Cancel = "CANCEL";
}

/// <summary>
/// <c>code</c> values used in <c>ERROR</c> payloads. <c>UNSUPPORTED_TYPE</c> and
/// <c>UNSUPPORTED_VERSION</c> are mandated by PROTOCOL.md §4; the rest are this
/// project's conventions for the failure modes listed in PROTOCOL.md §5 step 7.
/// </summary>
public static class ErrorCodes
{
    // Mandated by PROTOCOL.md §4.
    public const string UnsupportedType = "UNSUPPORTED_TYPE";
    public const string UnsupportedVersion = "UNSUPPORTED_VERSION";

    // Conventional codes for PROTOCOL.md §5 step 7 conditions.
    public const string ProtocolError = "PROTOCOL_ERROR";
    public const string IdentityMismatch = "IDENTITY_MISMATCH";
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    public const string ChecksumMismatch = "CHECKSUM_MISMATCH";
    public const string IoError = "IO_ERROR";
    public const string DiskFull = "DISK_FULL";
    public const string Cancelled = "CANCELLED";
    public const string InternalError = "INTERNAL_ERROR";
}
