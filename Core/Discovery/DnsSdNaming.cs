using System.Text;

namespace NearbyShare.Core.Discovery;

/// <summary>
/// DNS-SD instance-name sanitization and TXT-record helpers for PROTOCOL.md §1.
/// </summary>
public static class DnsSdNaming
{
    /// <summary>Maximum length of a DNS label / DNS-SD instance name, in bytes.</summary>
    public const int MaxInstanceNameBytes = 63;

    /// <summary>Maximum length of a single <c>key=value</c> TXT entry, in bytes.</summary>
    public const int MaxTxtEntryBytes = 255;

    /// <summary>
    /// Sanitizes a user-chosen device name into a legal DNS-SD instance name:
    /// control characters and DNS label separators removed, whitespace collapsed,
    /// truncated to 63 UTF-8 bytes on a character boundary.
    /// </summary>
    public static string SanitizeInstanceName(string? deviceName, string fallback = "NearbyShare")
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = fallback;
        }

        var builder = new StringBuilder(deviceName!.Length);
        bool lastWasSpace = false;
        foreach (char c in deviceName)
        {
            // '.' separates DNS labels and would split the instance name in two.
            // Control characters are illegal in DNS-SD instance names.
            if (c == '.' || char.IsControl(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (builder.Length == 0 || lastWasSpace)
                {
                    continue;
                }

                builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        string sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0)
        {
            sanitized = fallback;
        }

        return TruncateToBytes(sanitized, MaxInstanceNameBytes);
    }

    /// <summary>
    /// Appends a numeric suffix to de-duplicate an instance name against names
    /// already seen on the network, keeping the result within 63 bytes
    /// (PROTOCOL.md §1).
    /// </summary>
    public static string DeduplicateInstanceName(string sanitizedName, IReadOnlySet<string> takenNames)
    {
        if (!takenNames.Contains(sanitizedName))
        {
            return sanitizedName;
        }

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string tail = $" ({suffix})";
            int budget = MaxInstanceNameBytes - Encoding.UTF8.GetByteCount(tail);
            string candidate = TruncateToBytes(sanitizedName, Math.Max(1, budget)) + tail;
            if (!takenNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return sanitizedName;
    }

    /// <summary>
    /// Verifies a <c>key=value</c> TXT entry fits the 255-byte DNS-SD limit
    /// (PROTOCOL.md §1).
    /// </summary>
    public static bool IsTxtEntryWithinLimit(string key, string value) =>
        Encoding.UTF8.GetByteCount(key) + 1 + Encoding.UTF8.GetByteCount(value) <= MaxTxtEntryBytes;

    /// <summary>Splits a raw <c>key=value</c> TXT string. Returns false for a malformed entry.</summary>
    public static bool TryParseTxtEntry(string entry, out string key, out string value)
    {
        int separator = entry.IndexOf('=');
        if (separator <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = entry[..separator];
        value = entry[(separator + 1)..];
        return true;
    }

    /// <summary>Truncates a string to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    public static string TruncateToBytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        int used = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            string element = (string)enumerator.Current;
            int size = Encoding.UTF8.GetByteCount(element);
            if (used + size > maxBytes)
            {
                break;
            }

            builder.Append(element);
            used += size;
        }

        return builder.ToString();
    }
}
