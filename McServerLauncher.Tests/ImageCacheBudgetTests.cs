using System.IO.Compression;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Store images are decoded at the size they are drawn, and the memory cache has a byte budget.
/// </summary>
/// <remarks>
/// The download caps bounded the bytes fetched, not what they became: an 8 MB PNG can declare tens
/// of thousands of pixels a side. Every image was decoded at full size and up to 256 were kept, so
/// browsing a few galleries held hundreds of megabytes of screenshots shown 340 px high.
/// </remarks>
public class ImageCacheBudgetTests
{
    [Theory]
    [InlineData(64, 64, 256, 64)]          // small stays as it is
    [InlineData(1024, 1024, 256, 256)]     // an icon uploaded big is drawn small
    [InlineData(3840, 2160, 1600, 1600)]   // a 4K screenshot
    [InlineData(1080, 2400, 1600, 720)]    // a phone screenshot: its height is the long side
    [InlineData(1000, 39000, 1600, 41)]    // a strip under the pixel cap that used to decode at 156 MB
    public void ImagesAreDecodedNoBiggerThanTheyAreDrawn(int width, int height, int max, int decoded) =>
        Assert.Equal(decoded, ImageCache.DecodeWidth(width, height, max));

    [Theory]
    [InlineData(30000, 30000)]
    [InlineData(0, 100)]
    public void AbsurdSizesAreRefused(int width, int height) =>
        Assert.Null(ImageCache.DecodeWidth(width, height, ImageCache.GalleryDecodeWidth));

    [Fact]
    public void AnImageThatClaimsToBeEnormousIsNotDecoded()
    {
        // A real PNG header saying 30000 x 30000, with one tiny row of data: decoding it in full
        // would ask for 3.6 GB. Refused from the header, before anything is allocated.
        var png = PngClaiming(30000, 30000);

        // The header really is readable — so the refusal below is the size rule, not a broken file.
        using (var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(png)))
            Assert.Equal(30000, codec.Info.Width);

        Assert.Null(ImageCache.Decode(png, ImageCache.GalleryDecodeWidth));
    }

    [Fact]
    public void NothingIsEvictedWhileBothLimitsHold()
    {
        var entries = Entries((10, 1024), (20, 1024));
        Assert.Empty(ImageCache.Evict(entries, maxEntries: 10, budgetBytes: 4096));
    }

    [Fact]
    public void OverTheBudgetTheOldestGoFirstUntilItFits()
    {
        // Three screenshots of 40 MB against a 96 MB budget: only the oldest has to go.
        var mb = 1024L * 1024;
        var entries = Entries((1, 40 * mb), (2, 40 * mb), (3, 40 * mb));

        Assert.Equal(new[] { "1" }, ImageCache.Evict(entries, maxEntries: 256, budgetBytes: 96 * mb));
    }

    [Fact]
    public void OverTheCountItDropsToHalf()
    {
        var entries = Entries(Enumerable.Range(1, 9).Select(i => (i, 10L)).ToArray());

        var gone = ImageCache.Evict(entries, maxEntries: 8, budgetBytes: long.MaxValue).ToList();

        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, gone);   // 9 entries, keep 4
    }

    private static List<(string Key, DateTime Stored, long Bytes)> Entries(params (int Minute, long Bytes)[] items) =>
        items.Select(i => (i.Minute.ToString(), new DateTime(2026, 1, 1, 0, i.Minute, 0), i.Bytes)).ToList();

    /// <summary>A minimal valid PNG whose header claims <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static byte[] PngClaiming(int width, int height)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 0;    // greyscale
        Chunk(png, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(new byte[1 + width]);   // filter byte + one row: nowhere near the whole image
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        png.Write(length);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typeAndData);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(typeAndData)));
        png.Write(crc);
    }

    /// <summary>The CRC PNG puts after each chunk (the zlib/IEEE one).</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    private static void WriteBigEndian(byte[] target, int at, int value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }
}
