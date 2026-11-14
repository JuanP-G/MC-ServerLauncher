using System.Globalization;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The type colours, which mark each server in the list, have to be told apart at a glance and carry
/// white text. Fabric, NeoForge and Pumpkin were once all within 6° of the same orange; 12° is
/// enough when the two also differ in lightness (Paper red and Pumpkin orange are 15° apart).
/// </summary>
public class ServerTypeColourTests
{
    private static (double R, double G, double B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (int.Parse(h[..2], NumberStyles.HexNumber) / 255.0,
                int.Parse(h[2..4], NumberStyles.HexNumber) / 255.0,
                int.Parse(h[4..6], NumberStyles.HexNumber) / 255.0);
    }

    private static double Hue(string hex)
    {
        var (r, g, b) = Rgb(hex);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d == 0) return 0;
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    private static double ContrastWithWhite(string hex)
    {
        static double Lin(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        var (r, g, b) = Rgb(hex);
        var l = 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        return 1.05 / (l + 0.05);
    }

    [Fact]
    public void NoTwoTypesShareATone()
    {
        var all = ServerTypeCatalog.All;
        for (var i = 0; i < all.Length; i++)
            for (var j = i + 1; j < all.Length; j++)
            {
                var d = Math.Abs(Hue(all[i].BadgeColor) - Hue(all[j].BadgeColor));
                d = Math.Min(d, 360 - d);
                Assert.True(d >= 12, $"{all[i].DisplayName} and {all[j].DisplayName} are {d:0}° apart: too alike in the server list.");
            }
    }

    [Fact]
    public void WhiteTextReadsOnEveryTypeColour()
    {
        foreach (var entry in ServerTypeCatalog.All)
            Assert.True(ContrastWithWhite(entry.BadgeColor) >= 3.5,
                $"{entry.DisplayName}: white text on {entry.BadgeColor} is {ContrastWithWhite(entry.BadgeColor):0.0}:1.");
    }
}
