using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using McServerLauncher.Models;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Each server type's own logo, for the type picker.
/// </summary>
/// <remarks>
/// <para>
/// The projects' official logos, unedited, scaled to 144 px PNG (the SVG ones rasterised at a
/// uniform scale, since the app has no SVG renderer). Vanilla has none of its own, so it shows
/// Minecraft's grass block, the same one the website uses. Where each one comes from and on what
/// terms is in NOTICE.
/// </para>
/// <para>
/// Three of them are pixel art (the grass block, Fabric's scroll, NeoForge's fox): those are drawn
/// without smoothing, or a 52 px drawing blown up to the card blurs into a smear.
/// </para>
/// </remarks>
public static class ServerTypeLogos
{
    private static readonly Dictionary<ServerType, (string File, bool Pixel)> Files = new()
    {
        [ServerType.Vanilla] = ("vanilla", true),
        [ServerType.Paper] = ("paper", false),
        [ServerType.Purpur] = ("purpur", false),
        [ServerType.Fabric] = ("fabric", true),
        [ServerType.NeoForge] = ("neoforge", true),
        [ServerType.Forge] = ("forge", false),
        [ServerType.Pumpkin] = ("pumpkin", false),
    };

    private static readonly Dictionary<ServerType, Bitmap?> Cache = new();

    /// <summary>The logo of <paramref name="type"/>, or null for a type that has none.</summary>
    public static Bitmap? For(ServerType type)
    {
        if (Cache.TryGetValue(type, out var cached)) return cached;
        Bitmap? bitmap = null;
        if (Files.TryGetValue(type, out var file))
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new Uri($"avares://McServerLauncher/Resources/TypeLogos/{file.File}.png"));
                bitmap = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                Services.Safe.Note("TypeLogos", $"the logo of {type} could not be loaded", ex);
            }
        }
        return Cache[type] = bitmap;
    }

    /// <summary>Whether the logo is pixel art, to be scaled without smoothing.</summary>
    public static bool IsPixelArt(ServerType type) => Files.TryGetValue(type, out var f) && f.Pixel;

    /// <summary>The types that have a logo. For the tests.</summary>
    internal static IEnumerable<ServerType> WithLogo => Files.Keys;
}
