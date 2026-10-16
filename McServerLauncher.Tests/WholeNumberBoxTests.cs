using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// A number box left empty is not an error, and gets its number back.
/// </summary>
/// <remarks>
/// Deleting the minutes under "Minutes without players" showed
/// <c>System.InvalidCastException: Could not convert '(null)' (null) to System.Int32</c> under the
/// box: an empty <see cref="NumericUpDown"/> has a null value, and the setting behind it is an int.
/// Every box bound straight to an int setting did the same.
/// </remarks>
[Collection("avalonia")]
public class WholeNumberBoxTests(AvaloniaFixture ui)
{
    /// <summary>The idle box, and the draft's copy of the config it edits.</summary>
    private static (NumericUpDown Box, ServerConfig Config) ShowIdleBox(int minutes)
    {
        WholeNumberBoxes.Install();
        var draft = new ServerSettingsDraft(new ServerConfig { IdleShutdownMinutes = minutes });
        var section = new PowerSection(draft);
        var window = new Window { Content = section };
        window.Show();
        return (section.GetVisualDescendants().OfType<NumericUpDown>().Single(), draft.Config);
    }

    [Fact]
    public void EmptyingTheBoxIsNotAnError() => ui.Run(() =>
    {
        var (box, config) = ShowIdleBox(30);

        box.Value = null;   // what deleting the text does

        Assert.False(DataValidationErrors.GetHasErrors(box));
        Assert.Equal(30, config.IdleShutdownMinutes);   // nothing written while it is empty
    });

    [Fact]
    public void LeavingItEmptyPutsTheNumberBack() => ui.Run(() =>
    {
        var (box, config) = ShowIdleBox(30);

        box.Value = null;
        box.RaiseEvent(new RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));

        Assert.Equal(30m, box.Value);
        Assert.Equal(30, config.IdleShutdownMinutes);
    });

    [Fact]
    public void ANewNumberIsStillSaved() => ui.Run(() =>
    {
        var (box, config) = ShowIdleBox(30);

        box.Value = null;
        box.Value = 45;

        Assert.Equal(45, config.IdleShutdownMinutes);
    });

    /// <summary>Types <paramref name="text"/> into the max-RAM box of a Spanish app, and leaves it.</summary>
    private static (NumericUpDown Box, ServerConfig Config) TypeMaxRam(string text)
    {
        WholeNumberBoxes.Install();
        var draft = new ServerSettingsDraft(new ServerConfig { MinRamGb = 1, MaxRamGb = 4 });
        var section = new PerformanceSection(draft);
        new Window { Content = section }.Show();
        var box = section.GetVisualDescendants().OfType<NumericUpDown>().Single(b => b.Value == 4m);

        box.Text = text;
        WholeNumberBoxes.Restore(box);   // what leaving the box does
        return (box, draft.Config);
    }

    [Theory]
    [InlineData("1.5")]    // read as 15: in Spanish the dot separates thousands
    [InlineData("2,5")]
    [InlineData("1.000")]
    [InlineData("abc")]
    public void WhatIsNotAWholeNumberChangesNothingAndIsNotLeftOnScreen(string typed)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
            ui.Run(() =>
            {
                var (box, config) = TypeMaxRam(typed);

                Assert.Equal(4, config.MaxRamGb);
                Assert.Equal("4", box.Text);
                Assert.False(DataValidationErrors.GetHasErrors(box));
            });
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }

    [Fact]
    public void AWholeNumberIsTakenAsTyped() => ui.Run(() =>
    {
        var (_, config) = TypeMaxRam("12");

        Assert.Equal(12, config.MaxRamGb);
    });
}
