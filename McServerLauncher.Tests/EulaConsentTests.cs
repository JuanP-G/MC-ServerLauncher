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

    /// <summary>
    /// A folder typed in after one that had accepted it, and Add pressed at once, is asked about.
    /// </summary>
    /// <remarks>
    /// The box is judged on the folder last detected, and a typed path is only detected when the
    /// field loses focus — which pressing Add does, but the detection finishes after the click has
    /// started. Add used to go ahead with the box still hidden, take the server over, and leave it
    /// with no eula.txt and nobody having agreed to anything.
    /// </remarks>
    [Fact]
    public async Task AFolderTypedInJustBeforeAddIsAskedAbout()
    {
        var accepted = Path.Combine(_folder, "accepted");
        var typed = Path.Combine(_folder, "typed");
        Directory.CreateDirectory(accepted);
        Directory.CreateDirectory(typed);
        File.WriteAllText(Path.Combine(accepted, "eula.txt"), "eula=true\n");
        // An old Forge jar is a server the detection recognises from its name alone, version included.
        File.WriteAllText(Path.Combine(typed, "forge-1.12.2-14.23.5.2860.jar"), string.Empty);

        Window? window = null;
        NewServerView? panel = null;
        var completed = false;
        ui.Run(() =>
        {
            (window, panel) = Open();
            panel.Completed += _ => completed = true;
            panel.ChooseAdd();
            panel.ShowDetection(accepted, ServerDetection.Nothing);
            AvaloniaFixture.Pump();
            Assert.True(Named<Button>(panel, "AddButton").IsEnabled);

            Named<TextBox>(panel, "ExistingFolderBox").Text = typed;
            Named<Button>(panel, "AddButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });

        // The whole three seconds, not until the box shows: it showed before as well, just too late
        // to stop the takeover that came a moment after it.
        var asked = false;
        for (var i = 0; i < 30 && !completed; i++)
        {
            await Task.Delay(100);
            ui.Run(() =>
            {
                AvaloniaFixture.Pump();
                asked = Named<StackPanel>(panel!, "EulaPanel").IsVisible;
            });
        }

        Assert.False(completed, "the server was taken over before the EULA was accepted");
        Assert.True(asked, "the box never appeared for the folder that had not accepted the EULA");
        ui.Run(() => window!.Hide());
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
