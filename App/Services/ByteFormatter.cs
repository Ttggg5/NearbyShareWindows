namespace NearbyShare.App.Services;

/// <summary>Formats byte counts for display (offer summaries, progress bytes).</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Formats <paramref name="bytes"/> as e.g. "512 B", "3.4 MB".</summary>
    public static string Format(long bytes)
    {
        double value = bytes < 0 ? 0 : bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {Units[unit]}" : $"{value:0.#} {Units[unit]}";
    }
}
