using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using McServerLauncher.Behaviors;

namespace McServerLauncher.Tests;

/// <summary>
/// The MOTD renderer: Minecraft's colour and style codes turned into runs, with real controls.
/// </summary>
/// <remarks>
/// A run is written <c>text|colour|flags</c> (b bold, i italic, u underline, s strikethrough) and a
/// line break <c>BR</c>, so a whole rendering reads as one line in the assertion.
/// </remarks>
[Collection("avalonia")]
public class MinecraftMotdTests(AvaloniaFixture ui)
{
    private const string Grey = "#AAAAAA";

    private string[] Render(string? motd)
    {
        string[] result = [];
        ui.Run(() =>
        {
            var tb = new TextBlock();
            MinecraftMotd.SetText(tb, motd);
            result = Describe(tb);
        });
        return result;
    }

    internal static string[] Describe(TextBlock tb)
    {
        var parts = new List<string>();
        foreach (var inline in tb.Inlines ?? new InlineCollection())
        {
            if (inline is LineBreak) { parts.Add("BR"); continue; }

            var run = (Run)inline;
            var flags = new StringBuilder();
            if (run.FontWeight == FontWeight.Bold) flags.Append('b');
            if (run.FontStyle == FontStyle.Italic) flags.Append('i');
            foreach (var dec in run.TextDecorations ?? [])
                flags.Append(dec.Location == TextDecorationLocation.Underline ? 'u' : 's');
            var colour = ((SolidColorBrush)run.Foreground!).Color;
            parts.Add($"{run.Text}|#{colour.R:X2}{colour.G:X2}{colour.B:X2}|{flags}");
        }
        return parts.ToArray();
    }

    [Fact]
    public void PlainTextIsOneGreyRun() =>
        Assert.Equal([$"hello|{Grey}|"], Render("hello"));

    [Fact]
    public void NothingGivesNoRuns()
    {
        Assert.Empty(Render(null));
        Assert.Empty(Render(""));
    }

    [Fact]
    public void AColourCodeStartsANewRun() =>
        Assert.Equal([$"a|{Grey}|", "b|#FF5555|"], Render("a§cb"));

    [Fact]
    public void AmpersandWorksLikeTheSectionSign() =>
        Assert.Equal(["b|#FF5555|"], Render("&cb"));

    [Fact]
    public void StylesAccumulateUntilAColourResetsThem() =>
        Assert.Equal(["a|#FF5555|bu", "b|#55FF55|"], Render("§c§l§na§ab"));

    [Fact]
    public void AColourAfterABoldCodeDropsTheBold() =>
        Assert.Equal(["x|#FF5555|"], Render("&l&cx"));

    [Fact]
    public void ResetGoesBackToGreyAndNoStyle() =>
        Assert.Equal(["a|#FF5555|b", $"b|{Grey}|"], Render("§c§la§rb"));

    [Fact]
    public void ItalicAndStrikethrough() =>
        Assert.Equal(["a|#FFFFFF|is"], Render("§f§o§ma"));

    [Fact]
    public void ObfuscatedIsShownAsNormalText() =>
        Assert.Equal([$"a|{Grey}|", $"b|{Grey}|"], Render("a§kb"));

    [Fact]
    public void ALineBreakEndsTheRunAndKeepsTheColour() =>
        Assert.Equal(["a|#FF5555|", "BR", "b|#FF5555|"], Render("§ca\nb"));

    [Fact]
    public void BackslashNIsALineBreakToo() =>
        Assert.Equal([$"a|{Grey}|", "BR", $"b|{Grey}|"], Render("a\\nb"));

    [Fact]
    public void TheLongHexFormIsOneColourAndResetsStyles() =>
        Assert.Equal([$"a|{Grey}|", $"b|{Grey}|b", "z|#12AB34|"], Render("a§r§lb§x§1§2§a§b§3§4z"));

    [Fact]
    public void AnEscapedSectionSignIsUnescaped() =>
        Assert.Equal(["a|#FF5555|"], Render("\\u00a7ca"));

    [Fact]
    public void ALoneSectionSignAtTheEndIsKeptAsText() =>
        Assert.Equal([$"a§|{Grey}|"], Render("a§"));

    [Fact]
    public void AnUnknownCodeIsSwallowedWithoutChangingAnything() =>
        Assert.Equal([$"a|{Grey}|", $"b|{Grey}|"], Render("a§qb"));

    [Fact]
    public void TextSetAgainReplacesTheRuns()
    {
        string[] result = [];
        ui.Run(() =>
        {
            var tb = new TextBlock();
            MinecraftMotd.SetText(tb, "first");
            MinecraftMotd.SetText(tb, "§asecond");
            result = Describe(tb);
        });
        Assert.Equal(["second|#55FF55|"], result);
    }
}
