using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The new-server panel's second origin: taking over a folder that already holds a server.
/// </summary>
/// <remarks>
/// With real controls, because what can go wrong is the wiring: a mode switch that leaves the seed
/// box on screen for a world that already has one, or a picker left open to contradict the type the
/// folder was detected as.
/// </remarks>
[Collection("avalonia")]
public class CreateOrAddDialogTests(AvaloniaFixture ui)
{
    private static (Window Window, NewServerView Panel) Open()
    {
        var panel = new NewServerView();
        panel.ChooseCreate();
        AvaloniaFixture.WithoutIcons(panel);
        var window = new Window { Content = panel, Width = 640, Height = 940 };
        window.Show();
        window.UpdateLayout();
        return (window, panel);
    }

    /// <summary>A path in this system's own shape: a Windows one is a single file name on Linux.</summary>
    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "mcl-servers", name);

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void UseExisting(NewServerView panel)
    {
        panel.ChooseAdd();
        AvaloniaFixture.Pump();
    }

    private static readonly ServerDetection AFabricServer = new()
    {
        Type = ServerType.Fabric, GameVersion = "1.21.1", LoaderVersion = "0.16.2", JarFile = "fabric-server.jar",
        Port = 25570, MinRamGb = 3, MaxRamGb = 6, HasWorld = true, Jars = new[] { "fabric-server.jar" }
    };

    [Fact]
    public void TheOriginSwapsTheFolderFieldsAndHidesTheSeed()
    {
        ui.Run(() =>
        {
            var (window, panel) = Open();
            Assert.True(Named<StackPanel>(panel, "NewFolderPanel").IsVisible);
            Assert.False(Named<StackPanel>(panel, "ExistingPanel").IsVisible);
            Assert.True(Named<StackPanel>(panel, "SeedPanel").IsVisible);

            UseExisting(panel);

            Assert.False(Named<StackPanel>(panel, "NewFolderPanel").IsVisible);
            Assert.True(Named<StackPanel>(panel, "ExistingPanel").IsVisible);
            // An existing world already has its seed; level-seed is read once, when it is generated.
            Assert.False(Named<StackPanel>(panel, "SeedPanel").IsVisible);

            panel.ChooseCreate();
            AvaloniaFixture.Pump();
            Assert.True(Named<StackPanel>(panel, "SeedPanel").IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void WhatWasDetectedIsFilledInAndLocked()
    {
        ui.Run(() =>
        {
            var (window, panel) = Open();
            UseExisting(panel);

            panel.ShowDetection(Folder("survival"), AFabricServer);

            var picker = panel.GetVisualDescendants().OfType<ServerTypePicker>().Single();
            Assert.Equal(ServerType.Fabric, picker.SelectedType);
            Assert.False(picker.IsEnabled);

            var version = Named<ComboBox>(panel, "VersionCombo");
            Assert.Equal("1.21.1", (version.SelectedItem as MinecraftVersion)?.Id);
            Assert.False(version.IsEnabled);

            Assert.Equal(25570m, Named<NumericUpDown>(panel, "PortBox").Value);
            Assert.Equal(6m, Named<NumericUpDown>(panel, "MaxRamBox").Value);
            Assert.Equal("survival", Named<TextBox>(panel, "NameBox").Text);

            var summary = Named<TextBlock>(panel, "DetectionSummary");
            Assert.True(summary.IsVisible);
            Assert.Contains("Fabric 1.21.1", summary.Text);
            Assert.False(Named<StackPanel>(panel, "JarPanel").IsVisible);   // it knows what to run
            window.Close();
        });
    }

    [Fact]
    public void WhatWasNotDetectedStaysOpenToPick()
    {
        ui.Run(() =>
        {
            var (window, panel) = Open();
            UseExisting(panel);

            panel.ShowDetection(Folder("modpack"), new ServerDetection
            {
                Jars = new[] { "modpack-launcher.jar", "other.jar" }
            });

            var picker = panel.GetVisualDescendants().OfType<ServerTypePicker>().Single();
            Assert.True(picker.IsEnabled);
            Assert.True(Named<ComboBox>(panel, "VersionCombo").IsEnabled);

            var jars = Named<ComboBox>(panel, "JarCombo");
            Assert.True(Named<StackPanel>(panel, "JarPanel").IsVisible);
            Assert.Equal("modpack-launcher.jar", jars.SelectedItem);

            Assert.True(Named<TextBlock>(panel, "DetectionMissing").IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void GoingBackToCreatingUnlocksEverything()
    {
        ui.Run(() =>
        {
            var (window, panel) = Open();
            UseExisting(panel);
            panel.ShowDetection(Folder("survival"), AFabricServer);

            panel.ChooseCreate();
            AvaloniaFixture.Pump();

            Assert.True(panel.GetVisualDescendants().OfType<ServerTypePicker>().Single().IsEnabled);
            Assert.True(Named<ComboBox>(panel, "VersionCombo").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void TheButtonSaysWhatItWillDo()
    {
        ui.Run(() =>
        {
            var (window, panel) = Open();
            Assert.True(Named<Button>(panel, "CreateButton").IsVisible);
            Assert.False(Named<Button>(panel, "AddButton").IsVisible);

            UseExisting(panel);
            Assert.False(Named<Button>(panel, "CreateButton").IsVisible);
            Assert.True(Named<Button>(panel, "AddButton").IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void TheSummaryNamesWhatWasFound()
    {
        var text = NewServerView.Summary(AFabricServer);

        Assert.StartsWith("✔ Fabric 1.21.1", text);
        Assert.Contains("0.16.2", text);
        Assert.Contains("25570", text);
        Assert.Contains("6 GB", text);
        Assert.Equal(string.Empty, NewServerView.Summary(ServerDetection.Nothing));
    }

    [Fact]
    public void TheMainWindowHasOneButtonForBoth()
    {
        // Folded into one: the separate buttons and their commands are gone, and nothing may still
        // bind to a command that no longer exists — that would be a button doing nothing.
        var markup = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));
        Assert.DoesNotContain("AddServerCommand", markup);
        Assert.DoesNotContain("CreateServerCommand", markup);
        Assert.Null(typeof(McServerLauncher.ViewModels.MainViewModel).GetProperty("AddServerCommand"));
        Assert.Contains("ShowNewServerCommand", markup);
    }
}
