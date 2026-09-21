using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The create dialog's second mode: taking over a folder that already holds a server.
/// </summary>
/// <remarks>
/// With real controls, because what can go wrong is the wiring: a mode switch that leaves the seed
/// box on screen for a world that already has one, or a picker left open to contradict the type the
/// folder was detected as.
/// </remarks>
[Collection("avalonia")]
public class CreateOrAddDialogTests(AvaloniaFixture ui)
{
    private static CreateServerDialog Open()
    {
        var dialog = new CreateServerDialog();
        dialog.Show();
        dialog.Measure(new Avalonia.Size(640, 940));
        dialog.Arrange(new Avalonia.Rect(0, 0, 640, 940));
        return dialog;
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void UseExisting(CreateServerDialog dialog)
    {
        Named<RadioButton>(dialog, "ExistingModeRadio").IsChecked = true;
        AvaloniaFixture.Pump();
    }

    private static readonly ServerDetection AFabricServer = new()
    {
        Type = ServerType.Fabric, GameVersion = "1.21.1", LoaderVersion = "0.16.2", JarFile = "fabric-server.jar",
        Port = 25570, MinRamGb = 3, MaxRamGb = 6, HasWorld = true, Jars = new[] { "fabric-server.jar" }
    };

    [Fact]
    public void TheSwitchSwapsTheFolderFieldsAndHidesTheSeed()
    {
        ui.Run(() =>
        {
            var dialog = Open();
            Assert.True(Named<StackPanel>(dialog, "NewFolderPanel").IsVisible);
            Assert.False(Named<StackPanel>(dialog, "ExistingPanel").IsVisible);
            Assert.True(Named<StackPanel>(dialog, "SeedPanel").IsVisible);

            UseExisting(dialog);

            Assert.False(Named<StackPanel>(dialog, "NewFolderPanel").IsVisible);
            Assert.True(Named<StackPanel>(dialog, "ExistingPanel").IsVisible);
            // An existing world already has its seed; level-seed is read once, when it is generated.
            Assert.False(Named<StackPanel>(dialog, "SeedPanel").IsVisible);

            Named<RadioButton>(dialog, "NewModeRadio").IsChecked = true;
            AvaloniaFixture.Pump();
            Assert.True(Named<StackPanel>(dialog, "SeedPanel").IsVisible);
            dialog.Close();
        });
    }

    [Fact]
    public void WhatWasDetectedIsFilledInAndLocked()
    {
        ui.Run(() =>
        {
            var dialog = Open();
            UseExisting(dialog);

            dialog.ShowDetection(@"C:\servers\survival", AFabricServer);

            var picker = dialog.GetVisualDescendants().OfType<ServerTypePicker>().Single();
            Assert.Equal(ServerType.Fabric, picker.SelectedType);
            Assert.False(picker.IsEnabled);

            var version = Named<ComboBox>(dialog, "VersionCombo");
            Assert.Equal("1.21.1", (version.SelectedItem as MinecraftVersion)?.Id);
            Assert.False(version.IsEnabled);

            Assert.Equal(25570m, Named<NumericUpDown>(dialog, "PortBox").Value);
            Assert.Equal(6m, Named<NumericUpDown>(dialog, "MaxRamBox").Value);
            Assert.Equal("survival", Named<TextBox>(dialog, "NameBox").Text);

            var summary = Named<TextBlock>(dialog, "DetectionSummary");
            Assert.True(summary.IsVisible);
            Assert.Contains("Fabric 1.21.1", summary.Text);
            Assert.False(Named<StackPanel>(dialog, "JarPanel").IsVisible);   // it knows what to run
            dialog.Close();
        });
    }

    [Fact]
    public void WhatWasNotDetectedStaysOpenToPick()
    {
        ui.Run(() =>
        {
            var dialog = Open();
            UseExisting(dialog);

            dialog.ShowDetection(@"C:\servers\modpack", new ServerDetection
            {
                Jars = new[] { "modpack-launcher.jar", "other.jar" }
            });

            var picker = dialog.GetVisualDescendants().OfType<ServerTypePicker>().Single();
            Assert.True(picker.IsEnabled);
            Assert.True(Named<ComboBox>(dialog, "VersionCombo").IsEnabled);

            var jars = Named<ComboBox>(dialog, "JarCombo");
            Assert.True(Named<StackPanel>(dialog, "JarPanel").IsVisible);
            Assert.Equal("modpack-launcher.jar", jars.SelectedItem);

            Assert.True(Named<TextBlock>(dialog, "DetectionMissing").IsVisible);
            dialog.Close();
        });
    }

    [Fact]
    public void GoingBackToCreatingUnlocksEverything()
    {
        ui.Run(() =>
        {
            var dialog = Open();
            UseExisting(dialog);
            dialog.ShowDetection(@"C:\servers\survival", AFabricServer);

            Named<RadioButton>(dialog, "NewModeRadio").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.True(dialog.GetVisualDescendants().OfType<ServerTypePicker>().Single().IsEnabled);
            Assert.True(Named<ComboBox>(dialog, "VersionCombo").IsEnabled);
            dialog.Close();
        });
    }

    [Fact]
    public void TheButtonSaysWhatItWillDo()
    {
        ui.Run(() =>
        {
            var dialog = Open();
            var button = Named<Button>(dialog, "CreateButton");
            var create = button.Content;

            UseExisting(dialog);
            Assert.Equal(McServerLauncher.Localization.Localizer.Get("Cs_AddButton"), button.Content);

            Named<RadioButton>(dialog, "NewModeRadio").IsChecked = true;
            AvaloniaFixture.Pump();
            Assert.Equal(create, button.Content);
            dialog.Close();
        });
    }

    [Fact]
    public void TheSummaryNamesWhatWasFound()
    {
        var text = CreateServerDialog.Summary(AFabricServer);

        Assert.StartsWith("✔ Fabric 1.21.1", text);
        Assert.Contains("0.16.2", text);
        Assert.Contains("25570", text);
        Assert.Contains("6 GB", text);
        Assert.Equal(string.Empty, CreateServerDialog.Summary(ServerDetection.Nothing));
    }

    [Fact]
    public void TheMainWindowHasNoAddButtonAnyMore()
    {
        // Folded into "Create": the separate button and its command are gone, and nothing may still
        // bind to a command that no longer exists — that would be a button doing nothing.
        var markup = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));
        Assert.DoesNotContain("AddServerCommand", markup);
        Assert.Null(typeof(McServerLauncher.ViewModels.MainViewModel).GetProperty("AddServerCommand"));
        Assert.Contains("CreateServerCommand", markup);
    }
}
