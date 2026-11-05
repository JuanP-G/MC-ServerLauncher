using McServerLauncher.Services;
using SkiaSharp;

namespace McServerLauncher.Tests;

/// <summary>
/// A big picture has to shrink into an icon that still looks like it.
/// </summary>
/// <remarks>
/// SkiaSharp 3 turned <c>SKFilterQuality.High</c> into a cubic filter that only samples a few source
/// pixels per icon pixel, where 2.88 used mipmaps. Nothing fails when that happens: the icon just
/// comes out noisier, so this compares it with the real average of what each pixel covers.
/// </remarks>
public class ServerIconServiceTests
{
    private const int IconSide = 64;

    private static SKBitmap Noise(int side)
    {
        var random = new Random(7);
        var bitmap = new SKBitmap(side, side);
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        return bitmap;
    }

    /// <summary>How far each channel of the icon is, on average, from the block of the source it covers.</summary>
    private static double ErrorAgainstBlockAverage(SKBitmap source, SKBitmap icon)
    {
        var block = source.Width / IconSide;
        long error = 0;
        for (var y = 0; y < IconSide; y++)
            for (var x = 0; x < IconSide; x++)
            {
                int r = 0, g = 0, b = 0;
                for (var j = 0; j < block; j++)
                    for (var i = 0; i < block; i++)
                    {
                        var c = source.GetPixel(x * block + i, y * block + j);
                        r += c.Red; g += c.Green; b += c.Blue;
                    }
                var count = block * block;
                var got = icon.GetPixel(x, y);
                error += Math.Abs(got.Red - r / count) + Math.Abs(got.Green - g / count) + Math.Abs(got.Blue - b / count);
            }
        return error / (double)(IconSide * IconSide * 3);
    }

    [Fact]
    public void ABigPictureShrinksToWhatEachPixelCovers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcsl-icon-{Guid.NewGuid():N}.png");
        try
        {
            using var source = Noise(1024);
            using (var image = SKImage.FromBitmap(source))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(path))
                data.SaveTo(file);

            using var icon = SKBitmap.Decode(new ServerIconService().RenderIcon(path));

            Assert.Equal(IconSide, icon.Width);
            Assert.Equal(IconSide, icon.Height);
            Assert.True(ErrorAgainstBlockAverage(source, icon) < 2,
                "The icon does not average the source: is it sampling only a few pixels again?");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
