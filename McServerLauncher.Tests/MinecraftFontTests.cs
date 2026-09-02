using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Measuring a sign the way the game draws it.
/// </summary>
/// <remarks>
/// The whole reason this exists instead of a character count: Minecraft's font is variable-width, so
/// a warning based on how many characters you typed would nag about a line that fits and stay quiet
/// about one that does not. These pin the cases where counting and measuring disagree.
/// </remarks>
public class MinecraftFontTests
{
    [Fact]
    public void TheSameCountOfCharactersIsNotTheSameWidth()
    {
        // Ten of each. Counting says they are equal; the screen says one is nearly three times the
        // other, and the screen is the one people look at.
        var thin = MinecraftFont.Width(new string('i', 10), bold: false);
        var wide = MinecraftFont.Width(new string('M', 10), bold: false);

        Assert.Equal(20, thin);
        Assert.Equal(60, wide);
    }

    [Fact]
    public void BoldCostsAPixelPerGlyph()
    {
        // The game draws bold text twice, one pixel apart. On a full line that is real width, not a
        // rounding detail — it is what tips a line that just fitted over the edge.
        Assert.Equal(
            MinecraftFont.Width("Bienvenido", bold: false) + "Bienvenido".Length,
            MinecraftFont.Width("Bienvenido", bold: true));
    }

    [Fact]
    public void EachLineIsMeasuredOnItsOwn()
    {
        // Per line, because the list trims rather than wraps: a short first line buys the second one
        // no room at all.
        var runs = MotdDocument.Parse("iiii\nMMMM");
        var widths = MinecraftFont.LineWidths(runs);

        Assert.Equal(2, widths.Count);
        Assert.Equal(8, widths[0]);
        Assert.Equal(24, widths[1]);
        Assert.Equal(24, MinecraftFont.WidestLine(runs));
    }

    [Fact]
    public void ColourCodesTakeNoRoom()
    {
        // They are instructions, not ink. Counting them would warn about signs that fit perfectly,
        // and a sign with a lot of colours would be the one most likely to be wrongly flagged.
        //
        // Colours only, deliberately no §l here: bold is the one code that DOES cost width, so
        // slipping it in makes this assertion measure two things at once — which is what the first
        // version of this test did, and it failed for a reason that had nothing to do with colour.
        Assert.Equal(
            MinecraftFont.WidestLine(MotdDocument.Parse("Hola")),
            MinecraftFont.WidestLine(MotdDocument.Parse("§aH§bo§9l§da")));
    }

    [Fact]
    public void ARealSignFitsAndAnOverlongOneDoesNot()
    {
        // The two ends of the decision, with the limit that the list actually gives the text.
        Assert.True(MinecraftFont.WidestLine(MotdDocument.Parse("§aSurvival §7| §bVanilla §7| §6Amigos"))
                    <= MinecraftFont.ListWidth);

        Assert.True(MinecraftFont.WidestLine(MotdDocument.Parse(new string('W', 60)))
                    > MinecraftFont.ListWidth);
    }
}
