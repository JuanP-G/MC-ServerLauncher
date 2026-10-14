using System.IO;
using Avalonia.Media.Imaging;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>A server's <c>server-icon.png</c> as an image the window can show.</summary>
/// <remarks>
/// The server's card and the Appearance page each read it with a copy of the same code, one of them
/// with the file name written out by hand. <see cref="ServerIconService"/> makes the icon; this is
/// the one place that reads it back, kept out of the service because an image is a UI type.
/// </remarks>
internal static class ServerIconImage
{
    /// <summary>The icon in <paramref name="serverFolder"/>, or null when it has none or it cannot be read.</summary>
    /// <remarks>Read fully into memory, so the file is not locked and a new one is picked up.</remarks>
    public static Bitmap? Load(string serverFolder)
    {
        var path = Path.Combine(serverFolder, ServerIconService.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            return new Bitmap(file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>An icon not written yet, as <see cref="ServerIconService.RenderIcon"/> made it.</summary>
    public static Bitmap Decode(byte[] png)
    {
        using var memory = new MemoryStream(png);
        return new Bitmap(memory);
    }
}
