namespace NearbyShare.Core.Networking;

/// <summary>
/// Turns a peer-supplied <c>OFFER</c> file name into a safe local file name.
/// </summary>
/// <remarks>
/// The <c>name</c> field arrives from an untrusted peer on the LAN. Without this
/// a hostile or buggy sender could write outside the download folder via
/// <c>../</c> segments, an absolute path, an alternate data stream, or a reserved
/// Windows device name.
/// </remarks>
public static class FileNameSanitizer
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Reduces a peer-supplied name to a bare, legal file name with no path component.</summary>
    public static string Sanitize(string? name, string fallback = "received-file")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // Drop everything up to the last separator of either flavour, so
        // "..\\..\\Windows\\System32\\evil.dll" and "/etc/passwd" both collapse to
        // the leaf name.
        int lastSeparator = name.LastIndexOfAny(new[] { '/', '\\' });
        string leaf = lastSeparator >= 0 ? name[(lastSeparator + 1)..] : name;

        // An NTFS alternate data stream suffix ("report.txt:hidden") would write to
        // a different stream than the visible file.
        int colon = leaf.IndexOf(':');
        if (colon >= 0)
        {
            leaf = leaf[..colon];
        }

        var builder = new System.Text.StringBuilder(leaf.Length);
        foreach (char c in leaf)
        {
            builder.Append(char.IsControl(c) || IsInvalid(c) ? '_' : c);
        }

        // Windows silently strips trailing dots and spaces, which would let
        // "evil.exe." resolve to "evil.exe" after our checks.
        string sanitized = builder.ToString().TrimEnd('.', ' ').Trim();

        if (sanitized.Length == 0 || sanitized == "." || sanitized == "..")
        {
            return fallback;
        }

        string stem = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedWindowsNames.Contains(stem))
        {
            sanitized = "_" + sanitized;
        }

        // Leave room for the " (2)" de-duplication suffix within MAX_PATH-ish limits.
        const int maxLength = 200;
        if (sanitized.Length > maxLength)
        {
            string extension = Path.GetExtension(sanitized);
            sanitized = sanitized[..Math.Max(1, maxLength - extension.Length)] + extension;
        }

        return sanitized;
    }

    /// <summary>
    /// Resolves a collision-free full path inside <paramref name="directory"/>,
    /// appending " (2)", " (3)"… like Explorer does.
    /// </summary>
    public static string ResolveUniquePath(string directory, string sanitizedFileName)
    {
        string candidate = Path.Combine(directory, sanitizedFileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(sanitizedFileName);
        string extension = Path.GetExtension(sanitizedFileName);

        for (int i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static bool IsInvalid(char c) => Array.IndexOf(InvalidChars, c) >= 0;

    // Path.GetInvalidFileNameChars() is platform-dependent, and a file received on
    // Linux may later be opened on Windows, so the Windows set is applied always.
    private static readonly char[] InvalidChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
}
