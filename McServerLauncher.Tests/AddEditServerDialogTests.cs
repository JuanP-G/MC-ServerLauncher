using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Views;

namespace McServerLauncher.Tests;

/// <summary>
/// The edit dialog and the live config it is bound to.
/// </summary>
/// <remarks>
/// <para>
/// This dialog is handed the very <see cref="ServerConfig"/> the server list is showing and binds to
/// it two-way, so it has always written into it as the user typed. What it could not do was read
/// back: the config announced nothing, so a change made in code — the folder picker, a loader
/// install, an option switched off because the new type cannot do it — left the controls showing the
/// old value. The workaround was to null the <c>DataContext</c> and set it again after every one of
/// those, and one of them was missing.
/// </para>
/// <para>
/// None of this is reachable without real controls, which is the whole reason these run under
/// <see cref="AvaloniaFixture"/>: a unit test sees the model agreeing with itself and passes.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AddEditServerDialogTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcl-editdlg-" + Guid.NewGuid().ToString("N"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static AddEditServerDialog Open(ServerConfig config)
    {
        var dialog = new AddEditServerDialog(config);
        dialog.Show();
        dialog.Measure(new Avalonia.Size(620, 640));
        dialog.Arrange(new Avalonia.Rect(0, 0, 620, 640));
        return dialog;
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public void AChangeMadeInCodeReachesTheControls()
    {
        // The direction that needed the DataContext swap. The folder picker and the loader dialog
        // both write into the config from code; before, the boxes went on showing the old values.
        var config = new ServerConfig { Name = "viejo", FolderPath = Folder("uno") };

        ui.Run(() =>
        {
            var dialog = Open(config);
            var nameBox = Named<TextBox>(dialog, "NameBox");
            Assert.Equal("viejo", nameBox.Text);

            config.Name = "nuevo";
            AvaloniaFixture.Pump();

            Assert.Equal("nuevo", nameBox.Text);
            dialog.Close();
        });
    }

    [Fact]
    public void TypingReachesTheConfig()
    {
        // The direction that always worked, kept honest: making the binding live must not have
        // turned the two-way half around.
        var config = new ServerConfig { Name = "viejo", FolderPath = Folder("dos") };

        ui.Run(() =>
        {
            var dialog = Open(config);
            Named<TextBox>(dialog, "NameBox").Text = "escrito a mano";
            AvaloniaFixture.Pump();

            Assert.Equal("escrito a mano", config.Name);
            dialog.Close();
        });
    }

    [Fact]
    public void AnOptionTheNewTypeCannotDoIsUntickedOnScreenAndNotJustInTheModel()
    {
        // The bug this test exists for. UpdateTypeDependentOptions switches off what the type cannot
        // do, and it ran after the last re-bind — so it cleared the flag while the checkbox stayed
        // ticked. The user then pressed Save on a server that had quietly agreed to the opposite of
        // what was on screen.
        var config = new ServerConfig
        {
            Name = "convertido", FolderPath = Folder("tres"),
            Type = ServerType.Paper,        // no Hydraulic, no mod content
            BedrockModContentEnabled = true // as if it had been a Fabric server until a moment ago
        };

        ui.Run(() =>
        {
            var dialog = Open(config);
            var hydraulic = Named<CheckBox>(dialog, "HydraulicCheck");

            Assert.False(hydraulic.IsEnabled);              // Paper cannot do it
            Assert.False(config.BedrockModContentEnabled);  // so the dialog turned it off
            Assert.False(hydraulic.IsChecked);              // and said so, which is the part that failed

            dialog.Close();
        });
    }

    [Fact]
    public void CancellingPutsTheControlsBackToo()
    {
        // RestoreSnapshot assigns eighteen properties. It used to do that silently, so cancelling
        // restored the model while the boxes went on showing what had been typed.
        var config = new ServerConfig { Name = "original", FolderPath = Folder("cuatro"), MaxRamGb = 4 };

        ui.Run(() =>
        {
            var dialog = Open(config);
            var nameBox = Named<TextBox>(dialog, "NameBox");

            nameBox.Text = "a medio escribir";
            AvaloniaFixture.Pump();
            Assert.Equal("a medio escribir", config.Name);

            Named<Button>(dialog, "CancelButton").RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            AvaloniaFixture.Pump();

            Assert.Equal("original", config.Name);
            Assert.Equal("original", nameBox.Text);
        });
    }

    [Fact]
    public void TheFolderBoxWaitsUntilItLosesFocus()
    {
        // The one box that must not write as you type: the folder is the server's identity on disk,
        // and every letter would make the card behind the dialog re-read the port, the MOTD, the
        // icon, the mods folder and the backup list for a path that does not exist yet.
        var config = new ServerConfig { Name = "carpeta", FolderPath = Folder("cinco") };
        var original = config.FolderPath;

        ui.Run(() =>
        {
            var dialog = Open(config);
            var folderBox = Named<TextBox>(dialog, "FolderBox");
            var nameBox = Named<TextBox>(dialog, "NameBox");

            folderBox.Focus();
            folderBox.Text = Path.Combine(_root, "a-medio-");
            AvaloniaFixture.Pump();
            Assert.Equal(original, config.FolderPath);   // still the old one, mid-typing

            nameBox.Focus();                             // the commit the binding is waiting for
            AvaloniaFixture.Pump();
            Assert.Equal(Path.Combine(_root, "a-medio-"), config.FolderPath);

            dialog.Close();
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
