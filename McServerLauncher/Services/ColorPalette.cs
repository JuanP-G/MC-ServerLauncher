using Avalonia.Media;

namespace McServerLauncher.Services;

/// <summary>
/// The colours offered in the colour chooser, and how readable a colour is on the app's dark surfaces.
/// </summary>
/// <remarks>
/// <para>
/// The swatches are picked for this app, not a general palette: every one of them reads on the dark
/// card the console and the notices are drawn on. Three rows of the same eight hues, from vivid to
/// pastel, and a last row with the app's own defaults so going back to one is a click away.
/// </para>
/// <para>
/// Readability is the WCAG contrast ratio, the measure accessibility guidelines use: 21 for white on
/// black, 1 for a colour on itself. Text wants 4.5; a mark that only has to be seen — a level's dot
/// or badge — 3.
/// </para>
/// </remarks>
public static class ColorPalette
{
    /// <summary>The card the console and the notices are drawn on: translucent white over black.</summary>
    public static readonly Color DarkSurface = Color.Parse("#151515");

    /// <summary>Text: what console colours have to reach.</summary>
    public const double TextContrast = 4.5;

    /// <summary>A mark that only has to be seen: what the notice levels have to reach.</summary>
    public const double MarkContrast = 3.0;

    /// <summary>Four rows of eight, in reading order.</summary>
    public static readonly IReadOnlyList<string> Swatches = new[]
    {
        // vivid
        "#FF5C5C", "#FF8A3D", "#FFC233", "#4ADE80", "#2DD4BF", "#38BDF8", "#6E9BFF", "#C084FC",
        // soft
        "#F87171", "#FB923C", "#FACC15", "#86EFAC", "#5EEAD4", "#7DD3FC", "#93B4FF", "#D8A8FF",
        // pastel
        "#FCA5A5", "#FDBA74", "#FDE68A", "#BBF7D0", "#99F6E4", "#BAE6FD", "#C7D7FE", "#E9D5FF",
        // the app's own defaults, and two neutrals
        "#3FB950", "#2F6FB0", "#E3A82B", "#E05561", "#9CDCFE", "#C5A5F5", "#FFFFFF", "#B0B0B0",
    };

    /// <summary>How many swatches a row has.</summary>
    public const int RowLength = 8;

    /// <summary>The WCAG contrast ratio between two colours, from 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Whether <paramref name="color"/> reaches <paramref name="threshold"/> on <see cref="DarkSurface"/>.</summary>
    public static bool ReadsWellOnDark(Color color, double threshold) => Contrast(color, DarkSurface) >= threshold;

    /// <summary>Relative luminance as WCAG defines it.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>The colour as the settings store it: <c>#RRGGBB</c>, upper case, no alpha.</summary>
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A colour from <c>#RRGGBB</c>, or null when the text is not one (yet).</summary>
    public static Color? TryParse(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && Color.TryParse(hex, out var c) ? c : null;
}
