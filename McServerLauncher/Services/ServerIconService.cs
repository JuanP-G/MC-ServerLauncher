using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace McServerLauncher.Services;

/// <summary>
/// Genera el server-icon.png (64x64 PNG) de un servidor a partir de cualquier imagen, recortando al
/// centro en cuadrado y escalando. Es el icono que los jugadores ven en la lista de servidores.
/// </summary>
public class ServerIconService
{
    public void SetIconFromImage(string serverFolder, string sourceImagePath)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(sourceImagePath),
            BitmapCreateOptions.IgnoreImageCache,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        // Recorte central a cuadrado para no deformar.
        var side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        var x = (frame.PixelWidth - side) / 2;
        var y = (frame.PixelHeight - side) / 2;
        var cropped = new CroppedBitmap(frame, new Int32Rect(x, y, side, side));

        // Escalado a 64x64.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(cropped, new Rect(0, 0, 64, 64));

        var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var dest = Path.Combine(serverFolder, "server-icon.png");
        using var fs = File.Create(dest);
        encoder.Save(fs);
    }
}
