using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// The Appearance page of a server's settings reacting to what the user does, with real controls.
/// </summary>
/// <remarks>
/// The risky part is not any one piece: it is the text boxes, the document behind them and the
/// preview staying in step while the toolbar edits a selection the box is still holding. That only
/// exists once the controls are real, and a binding typo in these views fails silently.
/// </remarks>
[Collection("avalonia")]
public class AppearanceSectionTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-look-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private ServerConfig Server(string? motd, bool wake = false)
    {
        Directory.CreateDirectory(_folder);
        if (motd is not null) File.WriteAllText(Path.Combine(_folder, "server.properties"), "motd=" + motd + "\nmax-players=8\n");
        return new ServerConfig { Name = "ServerMC", FolderPath = _folder, WakeOnDemand = wake, Type = ServerType.Fabric, GameVersion = "26.2" };
    }

    /// <summary>
    /// Builds the page without showing it. Showing it would lay out the icon font, which the
    /// headless font manager cannot create (the same reason <c>MissingDependencyPanelTests</c> does
    /// not render its view); the controls and their wiring exist from the constructor on, which is
    /// all these tests need. How it looks is checked with a real renderer instead.
    /// </summary>
    private static AppearanceSection Open(ServerConfig config, bool running = false)
    {
        var section = new AppearanceSection(new ServerSettingsDraft(config), running);
        AvaloniaFixture.Pump();
        return section;
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"no hay un control llamado {name}");

    private static string PreviewMotd(Control dialog) => Named<ServerCardView>(dialog, "Preview").Motd ?? "";

    [Fact]
    public void ThePreviewShowsWhatTheFileHolds()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server(@"§6Survival\n§aVanilla"));

            Assert.Equal("Survival", Named<TextBox>(dialog, "Line1Box").Text);
            Assert.Equal("Vanilla", Named<TextBox>(dialog, "Line2Box").Text);
            Assert.Equal("§6Survival\n§aVanilla", PreviewMotd(dialog));

            var card = Named<ServerCardView>(dialog, "Preview");
            Assert.Equal("ServerMC", card.ServerName);
            Assert.Equal("Fabric", card.TypeText);
            Assert.Equal("26.2", card.Version);
            Assert.Equal("0/8", card.PlayerCount);
        });
    }

    [Fact]
    public void TypingUpdatesThePreviewAsYouGo()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("hola"));
            var line1 = Named<TextBox>(dialog, "Line1Box");

            line1.Text = "hola mundo";
            AvaloniaFixture.Pump();

            Assert.Equal("hola mundo", PreviewMotd(dialog));
        });
    }

    [Fact]
    public void AColourOnASelectionShowsInThePreviewAndSurvivesFurtherTyping()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("abcd"));
            var line1 = Named<TextBox>(dialog, "Line1Box");
            line1.Focus();
            line1.SelectionStart = 1;
            line1.SelectionEnd = 3;

            var gold = Named<Panel>(dialog, "ColorPanel").Children.OfType<Button>().First(b => (char)b.Tag! == '6');
            gold.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            AvaloniaFixture.Pump();

            Assert.Equal("a§6bc§rd", PreviewMotd(dialog));

            // Typing at the end must not disturb the coloured middle.
            line1.CaretIndex = 4;
            line1.Text = "abcde";
            AvaloniaFixture.Pump();

            Assert.Equal("a§6bc§rde", PreviewMotd(dialog));
        });
    }

    [Fact]
    public void BoldTogglesOffWhenTheWholeSelectionAlreadyHasIt()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("abc"));
            var line1 = Named<TextBox>(dialog, "Line1Box");
            line1.Focus();
            line1.SelectionStart = 0;
            line1.SelectionEnd = 3;
            var bold = Named<Avalonia.Controls.Primitives.ToggleButton>(dialog, "BoldButton");

            bold.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("§labc", PreviewMotd(dialog));

            bold.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("abc", PreviewMotd(dialog));
        });
    }

    [Fact]
    public void WithNothingSelectedTheNextTypedTextTakesTheChosenStyle()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("ab"));
            var line1 = Named<TextBox>(dialog, "Line1Box");
            line1.Focus();
            line1.CaretIndex = 2;
            var red = Named<Panel>(dialog, "ColorPanel").Children.OfType<Button>().First(b => (char)b.Tag! == 'c');
            red.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            line1.Text = "abc";
            AvaloniaFixture.Pump();

            Assert.Equal("ab§cc", PreviewMotd(dialog));
        });
    }

    [Fact]
    public void WithoutWakeThereIsNoSleepSelector()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("hola", wake: false));
            Assert.False(Named<StackPanel>(dialog, "PreviewModeRow").IsVisible);
        });
    }

    [Fact]
    public void WithWakeTheSleepingPreviewHasTheNoticeAndHidesLineTwo()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server(@"Linea uno\nLinea dos", wake: true));

            Assert.True(Named<StackPanel>(dialog, "PreviewModeRow").IsVisible);
            Assert.True(Named<RadioButton>(dialog, "PreviewAsleep").IsChecked);
            Assert.Equal("Linea uno\n" + WakeSign.Notice(starting: false), PreviewMotd(dialog));
            Assert.True(Named<TextBlock>(dialog, "Line2Note").IsVisible);

            Named<RadioButton>(dialog, "PreviewAwake").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.Equal("Linea uno\nLinea dos", PreviewMotd(dialog));
            Assert.False(Named<TextBlock>(dialog, "Line2Note").IsVisible);
        });
    }

    [Fact]
    public void ARunningWakeServerStartsOnTheAwakePreview()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("hola", wake: true), running: true);

            Assert.True(Named<RadioButton>(dialog, "PreviewAwake").IsChecked);
            Assert.True(Named<Border>(dialog, "RestartNote").IsVisible);
        });
    }

    [Fact]
    public void SavingWritesTheMotdAndTheNameButCancellingWritesNothing()
    {
        ui.Run(() =>
        {
            var config = Server("viejo");
            var path = Path.Combine(_folder, "server.properties");

            var cancelled = Open(config);
            Named<TextBox>(cancelled, "Line1Box").Text = "nuevo";
            Named<TextBox>(cancelled, "NameBox").Text = "Otro";
            AvaloniaFixture.Pump();
            // Discarded: the page is left without Save, and nothing is applied.

            Assert.Contains("motd=viejo", File.ReadAllText(path));
            Assert.Equal("ServerMC", config.Name);

            var saved = Open(config);
            Named<TextBox>(saved, "Line1Box").Text = "nuevo";
            Named<TextBox>(saved, "Line2Box").Text = "abajo";
            Named<TextBox>(saved, "NameBox").Text = "  Otro  ";
            AvaloniaFixture.Pump();

            saved.Draft.Apply();

            var text = File.ReadAllText(path);
            Assert.Contains(@"motd=nuevo\nabajo", text);
            Assert.Contains("max-players=8", text);       // the rest of the file is untouched
            Assert.Equal("Otro", config.Name);
        });
    }

    [Fact]
    public void SavingWithoutTouchingTheMotdLeavesTheFileAlone()
    {
        ui.Run(() =>
        {
            // A hand-written value in the raw form: re-saving it normalised would be a gratuitous edit.
            var config = Server("§6Hola");
            var path = Path.Combine(_folder, "server.properties");
            var before = File.ReadAllText(path);

            var dialog = Open(config);
            dialog.Draft.Apply();

            Assert.Equal(before, File.ReadAllText(path));
        });
    }

    [Fact]
    public void ServerWithoutPropertiesIsNotGivenOneByAnUntouchedSave()
    {
        ui.Run(() =>
        {
            var config = Server(motd: null);

            var dialog = Open(config);
            dialog.Draft.Apply();

            Assert.False(File.Exists(Path.Combine(_folder, "server.properties")));
        });
    }

    [Fact]
    public void ThePickedImageIsOnlyWrittenOnSaveAndRemovingDeletesIt()
    {
        ui.Run(() =>
        {
            var config = Server("hola");
            var source = Path.Combine(_folder, "source.png");
            using (var bmp = new SkiaSharp.SKBitmap(100, 80))
            using (var image = SkiaSharp.SKImage.FromBitmap(bmp))
            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(source, data.ToArray());
            var icon = Path.Combine(_folder, "server-icon.png");

            var dialog = Open(config);
            dialog.UseImage(source);
            AvaloniaFixture.Pump();

            Assert.False(File.Exists(icon));                       // previewed, not written
            Assert.NotNull(Named<ServerCardView>(dialog, "Preview").Icon);

            dialog.Draft.Apply();
            Assert.True(File.Exists(icon));

            var again = Open(config);
            Named<Button>(again, "RemoveImageButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            again.Draft.Apply();
            Assert.False(File.Exists(icon));
        });
    }

    [Fact]
    public void AStrayCodeTypedInABoxIsDroppedFromTheBoxToo()
    {
        ui.Run(() =>
        {
            var dialog = Open(Server("hola"));
            var line1 = Named<TextBox>(dialog, "Line1Box");

            line1.Text = "ho§la";
            AvaloniaFixture.Pump();

            Assert.Equal("hola", line1.Text);
            Assert.Equal("hola", PreviewMotd(dialog));
        });
    }
}
