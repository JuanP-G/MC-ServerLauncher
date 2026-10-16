using System.Globalization;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// One way to write a size on disk, where three view models each had their own.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1024L * 1024 - 1, "1024 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(3L * 1024 * 1024 + 512 * 1024, "3.5 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(5L * 1024 * 1024 * 1024 + 256L * 1024 * 1024, "5.3 GB")]
    public void ASizeIsWrittenInThePowersOf1024(long bytes, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, ByteSize.Format(bytes));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    /// <summary>
    /// A backup folder past a gigabyte used to read "1500 MB" in the history size, because that one
    /// formatter stopped at MB; the backups tab had its own that went on to GB.
    /// </summary>
    [Fact]
    public void AGigabyteIsNotWrittenInMegabytes()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.EndsWith(" GB", ByteSize.Format(1500L * 1024 * 1024));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void TheDecimalSeparatorFollowsTheCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            Assert.Equal("1,5 KB", ByteSize.Format(1536));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
