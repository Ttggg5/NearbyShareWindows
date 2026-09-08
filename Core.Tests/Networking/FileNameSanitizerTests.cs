using NearbyShare.Core.Networking;
using Xunit;

namespace NearbyShare.Core.Tests.Networking;

/// <summary>
/// The <c>name</c> field of an <c>OFFER</c> is attacker-controlled input from the
/// LAN, so these cases matter for more than tidiness.
/// </summary>
public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("report.txt", "report.txt")]
    [InlineData("holiday photo.jpg", "holiday photo.jpg")]
    [InlineData("假期照片.jpg", "假期照片.jpg")]
    public void OrdinaryNamesPassThroughUnchanged(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\victim\Desktop\note.txt", "note.txt")]
    [InlineData("/var/tmp/note.txt", "note.txt")]
    [InlineData(@"subdir\file.txt", "file.txt")]
    public void PathComponentsAreStrippedSoNothingEscapesTheFolder(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void AlternateDataStreamSuffixesAreRemoved()
    {
        // "report.txt:hidden" would otherwise write to an invisible NTFS stream.
        Assert.Equal("report.txt", FileNameSanitizer.Sanitize("report.txt:hidden"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT1.dat")]
    [InlineData("NUL")]
    public void ReservedWindowsDeviceNamesArePrefixed(string input)
    {
        string result = FileNameSanitizer.Sanitize(input);
        Assert.StartsWith("_", result);
    }

    [Fact]
    public void TrailingDotsAndSpacesAreTrimmed()
    {
        // Windows silently strips these, so "evil.exe." would resolve to "evil.exe".
        Assert.Equal("evil.exe", FileNameSanitizer.Sanitize("evil.exe. "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/")]
    public void DegenerateNamesFallBack(string? input)
    {
        Assert.Equal("received-file", FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void InvalidCharactersAreReplacedRatherThanDropped()
    {
        Assert.Equal("a_b_c_d.txt", FileNameSanitizer.Sanitize("a<b>c|d.txt"));
    }

    [Fact]
    public void ControlCharactersAreReplaced()
    {
        Assert.Equal("a_b.txt", FileNameSanitizer.Sanitize("a\u0001b.txt"));
    }

    [Fact]
    public void VeryLongNamesAreTruncatedButKeepTheirExtension()
    {
        string result = FileNameSanitizer.Sanitize(new string('x', 400) + ".txt");

        Assert.True(result.Length <= 200);
        Assert.EndsWith(".txt", result);
    }

    [Fact]
    public void ResolveUniquePathAppendsACounterInsteadOfOverwriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nearbyshare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Equal(Path.Combine(directory, "a.txt"), FileNameSanitizer.ResolveUniquePath(directory, "a.txt"));

            File.WriteAllText(Path.Combine(directory, "a.txt"), "x");
            Assert.Equal(Path.Combine(directory, "a (2).txt"), FileNameSanitizer.ResolveUniquePath(directory, "a.txt"));

            File.WriteAllText(Path.Combine(directory, "a (2).txt"), "x");
            Assert.Equal(Path.Combine(directory, "a (3).txt"), FileNameSanitizer.ResolveUniquePath(directory, "a.txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
