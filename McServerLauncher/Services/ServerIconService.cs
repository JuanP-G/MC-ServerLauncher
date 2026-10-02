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
    public const string FileName = "server-icon.png";

    public void SetIconFromImage(string serverFolder, string sourceImagePath) =>
        WriteIcon(serverFolder, RenderIcon(sourceImagePath));

    /// <summary>The 64x64 PNG a server would get from this image, without touching any server.</summary>
    /// <remarks>
    /// Separate from writing so the appearance editor can show the result straight away and only
    /// put it on disk when the user accepts: picking an image and then cancelling must leave the
    /// server exactly as it was.
    /// </remarks>
    public byte[] RenderIcon(string sourceImagePath)
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
        return data.ToArray();
    }

    public void WriteIcon(string serverFolder, byte[] png) =>
        File.WriteAllBytes(Path.Combine(serverFolder, FileName), png);

    /// <summary>Deletes the icon, so the server list falls back to the game's default.</summary>
    public void RemoveIcon(string serverFolder)
    {
        var path = Path.Combine(serverFolder, FileName);
        if (File.Exists(path)) File.Delete(path);
    }
}
