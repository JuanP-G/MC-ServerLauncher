using Avalonia.Controls;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// What the screens show, and stop showing, for a server that is not Java (Pumpkin).
/// </summary>
/// <remarks>
/// With real controls, because none of it is visible to a unit test: the bindings are not compiled,
/// and a panel that was never hidden builds just as well as one that was.
/// </remarks>
[Collection("avalonia")]
public sealed class NativeServerViewTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "mcsl-native-view-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private ServerConfig PumpkinServer()
    {
        Directory.CreateDirectory(_folder);
        PumpkinConfig.WriteInitial(_folder, 25599, "Prueba", seed: null);
        return new ServerConfig
        {
            Name = "Prueba",
            FolderPath = _folder,
            Type = ServerType.Pumpkin,
            JarFile = PumpkinService.ExecutableName,
            GameVersion = "26.3",
            ModLoaderVersion = "0.2.2"
        };
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    // --- creating one ---

    private static (Window Dialog, NewServerView View, Dictionary<ServerType, RadioButton> Cards) OpenCreate(
        bool existing = false)
    {
        var view = new NewServerView();
        if (existing) view.ChooseAdd();
        else view.ChooseCreate();
        AvaloniaFixture.WithoutIcons(view);
        var dialog = new Window { Content = view };
        dialog.Show();
        dialog.Measure(new Avalonia.Size(640, 940));
        dialog.Arrange(new Avalonia.Rect(0, 0, 640, 940));

        var picker = dialog.GetVisualDescendants().OfType<ServerTypePicker>().Single();
        return (dialog, view, picker.GetVisualDescendants().OfType<RadioButton>()
            .ToDictionary(c => (ServerType)c.Tag!, c => c));
    }

    [Fact]
    public void PickingPumpkinHidesTheVersionAndTheMemoryAndShowsTheWarning()
    {
        ui.Run(() =>
        {
            var (dialog, _, cards) = OpenCreate();
            var version = Named<StackPanel>(dialog, "VersionPanel");
            var memory = Named<StackPanel>(dialog, "MemoryPanel");
            var warning = Named<Border>(dialog, "ExperimentalBox");
            var note = Named<TextBlock>(dialog, "NativeVersionNote");

            // Vanilla, the default: everything a Java server is asked.
            Assert.True(version.IsVisible);
            Assert.True(memory.IsVisible);
            Assert.False(warning.IsVisible);
            Assert.False(note.IsVisible);

            cards[ServerType.Pumpkin].IsChecked = true;

            Assert.False(version.IsVisible);   // the release decides the Minecraft version
            Assert.False(memory.IsVisible);    // no heap to size
            Assert.True(warning.IsVisible);
            Assert.True(note.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(Named<TextBlock>(dialog, "ExperimentalText").Text));

            // Geyser is not offered, and the reason given is Pumpkin's own.
            Assert.False(Named<CheckBox>(dialog, "CrossplayCheck").IsEnabled);
            Assert.Equal(McServerLauncher.Localization.Localizer.Get("Crossplay_NativeBedrock"),
                Named<TextBlock>(dialog, "CrossplayWhyNot").Text);

            // And back: nothing of it sticks to the next type.
            cards[ServerType.Paper].IsChecked = true;
            Assert.True(version.IsVisible);
            Assert.True(memory.IsVisible);
            Assert.False(warning.IsVisible);
            Assert.False(note.IsVisible);
        });
    }

    [Fact]
    public void TheCardCarriesTheExperimentalBadge()
    {
        ui.Run(() =>
        {
            var (_, _, cards) = OpenCreate();
            var badge = McServerLauncher.Localization.Localizer.Get("Badge_Experimental");

            foreach (var (type, card) in cards)
            {
                var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");
                Assert.Equal(type == ServerType.Pumpkin, texts.Contains(badge));
            }
        });
    }

    [Fact]
    public void AnExistingFolderCannotBeTakenOverAsPumpkin()
    {
        // Nothing in a Pumpkin folder says which Minecraft it serves, and the questions asked about
        // an existing folder are about Java. Offering the card would be offering a dead end.
        ui.Run(() =>
        {
            var (_, _, existing) = OpenCreate(existing: true);
            Assert.DoesNotContain(ServerType.Pumpkin, existing.Keys);
            Assert.Contains(ServerType.Paper, existing.Keys);

            var (_, _, creating) = OpenCreate();
            Assert.Contains(ServerType.Pumpkin, creating.Keys);
        });
    }

    // --- its card and its settings ---

    [Fact]
    public void ThePortAndTheMessageOnItsCardComeFromPumpkinToml()
    {
        ui.Run(() =>
        {
            var server = new ServerViewModel(PumpkinServer());

            Assert.Equal("25599", server.PortText);
            Assert.Equal("Prueba", server.MotdText);
            Assert.True(server.IsNative);
            Assert.False(server.IsModded);    // no Mods tab: nothing the store has runs on it
        });
    }

    [Fact]
    public void ItsSettingsOpenOnAPageItHasAndBuildEveryPageOffered()
    {
        ui.Run(() =>
        {
            // Asked for the page a Java server opens on, which Pumpkin does not have.
            var view = new ServerSettingsView(new ServerViewModel(PumpkinServer()), ServerSettingsPage.Game);
            Assert.Equal(ServerSettingsPage.Appearance, view.Page);

            foreach (var page in Enum.GetValues<ServerSettingsPage>()
                         .Where(p => ServerSettingsPages.IsOffered(p, ServerType.Pumpkin)))
            {
                view.Show(page);
                Assert.Equal(page, view.Page);
            }
            Assert.False(view.IsDirty);   // looking at every page changed nothing
        });
    }

    [Fact]
    public void NetworkAndAdvancedShowTheTomlCardInsteadOfWhatOnlyJavaHas()
    {
        ui.Run(() =>
        {
            var view = new ServerSettingsView(new ServerViewModel(PumpkinServer()), ServerSettingsPage.Network);

            var network = view.Section(ServerSettingsPage.Network);
            Assert.True(network.FindControl<NativeConfigCard>("NativeCard")!.IsVisible);
            Assert.False(network.FindControl<Border>("PortCard")!.IsVisible);
            Assert.False(network.FindControl<Border>("OnlineModeCard")!.IsVisible);
            Assert.False(network.FindControl<Border>("WhitelistCard")!.IsVisible);

            var advanced = view.Section(ServerSettingsPage.Advanced);
            Assert.True(advanced.FindControl<NativeConfigCard>("NativeCard")!.IsVisible);
            Assert.False(advanced.FindControl<Border>("JarCard")!.IsVisible);
            Assert.False(advanced.FindControl<Border>("ExtraArgsCard")!.IsVisible);

            var appearance = view.Section(ServerSettingsPage.Appearance);
            Assert.False(appearance.FindControl<Border>("MotdCard")!.IsVisible);
        });
    }

    [Fact]
    public void AJavaServerKeepsEverythingItHad()
    {
        ui.Run(() =>
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "server.properties"), "server-port=25565\n");
            var config = new ServerConfig { Name = "Paper", FolderPath = _folder, Type = ServerType.Paper };
            var view = new ServerSettingsView(new ServerViewModel(config), ServerSettingsPage.Game);
            Assert.Equal(ServerSettingsPage.Game, view.Page);

            var network = view.Section(ServerSettingsPage.Network);
            Assert.False(network.FindControl<NativeConfigCard>("NativeCard")!.IsVisible);
            Assert.True(network.FindControl<Border>("PortCard")!.IsVisible);

            var advanced = view.Section(ServerSettingsPage.Advanced);
            Assert.False(advanced.FindControl<NativeConfigCard>("NativeCard")!.IsVisible);
            Assert.True(advanced.FindControl<Border>("JarCard")!.IsVisible);

            Assert.True(view.Section(ServerSettingsPage.Appearance).FindControl<Border>("MotdCard")!.IsVisible);
        });
    }
}
