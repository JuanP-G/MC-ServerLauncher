using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The Minecraft EULA is accepted by the person creating the server, with a box they tick.
/// </summary>
/// <remarks>
/// The app used to write <c>eula=true</c> on their behalf, with nothing on screen but a line in the
/// progress log. Mojang's EULA asks the person running the server to agree to it, so the button that
/// writes the file stays shut until they have.
/// </remarks>
[Collection("avalonia")]
public class EulaConsentTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-eula-" + Guid.NewGuid().ToString("N"));

    private static (Window Window, NewServerView Panel) Open()
    {
        var panel = new NewServerView();
        AvaloniaFixture.WithoutIcons(panel);
        var window = new Window { Content = panel, Width = 900, Height = 940 };
        window.Show();
        return (window, panel);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public void CreatingWaitsForTheBoxToBeTicked()
    {
        ui.Run(() =>
        {
            var (window, panel) = Open();
            panel.ChooseCreate();
            AvaloniaFixture.Pump();

            Assert.True(Named<StackPanel>(panel, "EulaPanel").IsVisible);
            Assert.False(Named<Button>(panel, "CreateButton").IsEnabled);

            Named<CheckBox>(panel, "EulaCheck").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.True(Named<Button>(panel, "CreateButton").IsEnabled);
            window.Hide();
        });
    }

    [Fact]
    public void AFolderThatAcceptedItAlreadyIsNotAskedAgain()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "eula.txt"), "#comment\neula = true\n");

        ui.Run(() =>
        {
            var (window, panel) = Open();
            panel.ChooseAdd();
            panel.ShowDetection(_folder, ServerDetection.Nothing);
            AvaloniaFixture.Pump();

            Assert.False(Named<StackPanel>(panel, "EulaPanel").IsVisible);
            Assert.True(Named<Button>(panel, "AddButton").IsEnabled);
            window.Hide();
        });
    }

    [Fact]
    public void AFolderThatHasNotIsAsked()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "eula.txt"), "eula=false\n");

        ui.Run(() =>
        {
            var (window, panel) = Open();
            panel.ChooseAdd();
            panel.ShowDetection(_folder, ServerDetection.Nothing);
            AvaloniaFixture.Pump();

            Assert.True(Named<StackPanel>(panel, "EulaPanel").IsVisible);
            Assert.False(Named<Button>(panel, "AddButton").IsEnabled);
            window.Hide();
        });
    }

    [Fact]
    public void WhatIsWrittenIsWhatIsRead()
    {
        Directory.CreateDirectory(_folder);
        Assert.False(ServerCreationService.EulaAccepted(_folder));

        new ServerCreationService().WriteEula(_folder);

        Assert.True(ServerCreationService.EulaAccepted(_folder));
        Assert.Contains(AppLinks.MinecraftEula, File.ReadAllText(Path.Combine(_folder, "eula.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
