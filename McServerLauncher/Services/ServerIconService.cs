using System.IO;
using SkiaSharp;

namespace McServerLauncher.Services;

/// <summary>
/// Generates a server's server-icon.png (64x64 PNG) from any image, cropping it to a centered
/// square and scaling. This is the icon players see in the server list. Uses SkiaSharp so it works
/// on Windows and Linux.
/// </summary>
public class ServerIconService
{
    public void SetIconFromImage(string serverFolder, string sourceImagePath)
    {
        using var input = File.OpenRead(sourceImagePath);
        using var original = SKBitmap.Decode(input)
            ?? throw new InvalidOperationException("Could not read the selected image.");

        // Centered square crop to avoid distortion.
        var side = Math.Min(original.Width, original.Height);
        var x = (original.Width - side) / 2;
        var y = (original.Height - side) / 2;

        using var cropped = new SKBitmap(side, side);
        original.ExtractSubset(cropped, SKRectI.Create(x, y, side, side));

        // Scale to 64x64 and encode as PNG.
        using var resized = cropped.Resize(new SKImageInfo(64, 64), SKFilterQuality.High);
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var dest = Path.Combine(serverFolder, "server-icon.png");
        using var output = File.Create(dest);
        data.SaveTo(output);
    }
}
