using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>The palette of the colour chooser, and how readable a colour is.</summary>
public class ColorPaletteTests
{
    [Fact]
    public void ContrastIsTheWcagRatio()
    {
        Assert.Equal(21, ColorPalette.Contrast(Colors.White, Colors.Black), 1);
        Assert.Equal(1, ColorPalette.Contrast(Colors.Red, Colors.Red), 3);
    }

    [Fact]
    public void ThePaletteIsFourRowsOfEightWithoutRepeats()
    {
        Assert.Equal(4 * ColorPalette.RowLength, ColorPalette.Swatches.Count);
        Assert.Equal(ColorPalette.Swatches.Count, ColorPalette.Swatches.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ColorPalette.Swatches, hex => Assert.Matches("^#[0-9A-F]{6}$", hex));
    }

    [Fact]
    public void EverySwatchCanBeSeenAndTheFirstThreeRowsCanBeRead()
    {
        // The point of offering these and not just a spectrum: none of them disappears on the dark
        // card. The last row holds the app's own defaults, which only have to be visible marks.
        for (var i = 0; i < ColorPalette.Swatches.Count; i++)
        {
            var color = Color.Parse(ColorPalette.Swatches[i]);
            Assert.True(ColorPalette.ReadsWellOnDark(color, ColorPalette.MarkContrast), ColorPalette.Swatches[i]);
            if (i < 3 * ColorPalette.RowLength)
                Assert.True(ColorPalette.ReadsWellOnDark(color, ColorPalette.TextContrast), ColorPalette.Swatches[i]);
        }
    }

    [Fact]
    public void OnlyAWholeCodeIsAColour()
    {
        Assert.Null(ColorPalette.TryParse("#E0"));
        Assert.Null(ColorPalette.TryParse("E05561"));
        Assert.Equal("#E05561", ColorPalette.ToHex(ColorPalette.TryParse("#e05561")!.Value));
    }
}

/// <summary>The colour chooser: picking, typing and going back.</summary>
[Collection("avalonia")]
public class ColorChooserTests(AvaloniaFixture ui)
{
    private static T Named<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException(name);

    [Fact]
    public void PickingASwatchChangesTheColourAndMarksTheSwatch() =>
        ui.Run(() =>
        {
            var chooser = new ColorChooser { Hex = "#3FB950" };

            chooser.Pick("#FF5C5C");

            Assert.Equal("#FF5C5C", chooser.Hex);
            Assert.Equal("#FF5C5C", Named<TextBox>(chooser, "HexBox").Text);
            var marked = Named<UniformGrid>(chooser, "SwatchGrid").Children.Where(c => c.Classes.Contains("on")).ToList();
            Assert.Equal("#FF5C5C", (string)Assert.Single(marked).Tag!);
        });

    [Fact]
    public void EscapeGoesBackToTheColourItOpenedOn() =>
        ui.Run(() =>
        {
            var chooser = new ColorChooser { Hex = "#3FB950" };
            chooser.Opened();
            chooser.Pick("#FF5C5C");
            chooser.Pick("#6E9BFF");

            chooser.Revert();

            Assert.Equal("#3FB950", chooser.Hex);
        });

    [Fact]
    public void AHalfTypedCodeNeverReachesTheSetting() =>
        ui.Run(() =>
        {
            var chooser = new ColorChooser { Hex = "#3FB950" };
            var box = Named<TextBox>(chooser, "HexBox");

            box.Text = "#E0";
            AvaloniaFixture.Pump();   // TextChanged arrives through the dispatcher
            Assert.Equal("#3FB950", chooser.Hex);

            box.Text = "#E05561";
            AvaloniaFixture.Pump();
            Assert.Equal("#E05561", chooser.Hex);
        });

    [Fact]
    public void ItSaysWhenAColourWillBeHardToRead() =>
        ui.Run(() =>
        {
            var chooser = new ColorChooser { Hex = "#1A1A40", Threshold = ColorPalette.TextContrast };
            var line = Named<TextBlock>(chooser, "ContrastText");
            Assert.Equal(McServerLauncher.Localization.Localizer.Get("Col_TooDark"), line.Text);

            chooser.Pick("#FDE68A");
            Assert.Equal(McServerLauncher.Localization.Localizer.Get("Col_ReadsWell"), line.Text);
        });

    [Fact]
    public void EverySettingsColourUsesTheChooser()
    {
        var xaml = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "SettingsView.axaml"));

        foreach (var prop in new[] { "ColorSuccess", "ColorInfo", "ColorWarning", "ColorError", "ConsoleChatColor", "ConsolePlayersColor" })
            Assert.Matches($@"<views:ColorChooser[^>]*Hex=""\{{Binding {prop}, Mode=TwoWay\}}""", Regex.Replace(xaml, @"\s+", " "));
        Assert.DoesNotContain("HexBrushConverter", xaml);
    }
}

/// <summary>"Check for updates" while it is checking.</summary>
public class UpdateCheckLookTests
{
    [Fact]
    public void ItTurnsARingAndRunsALineInsteadOfASqueezedBar()
    {
        // It used to be a 20 px progress bar squeezed into the icon's slot.
        var xaml = Regex.Replace(File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "AboutView.axaml")), @"\s+", " ");

        Assert.DoesNotMatch(@"<ProgressBar [^>]*Width=""20""", xaml);
        Assert.Matches(@"<Panel Classes=""spinner""[^>]*IsVisible=""\{Binding IsCheckingUpdates\}""", xaml);
        Assert.Matches(@"<ProgressBar Classes=""edgeload""[^>]*IsVisible=""\{Binding IsCheckingUpdates\}""", xaml);
        Assert.Contains("Panel.spinner[IsVisible=True]", File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Styles", "Motion.axaml")));
    }
}
