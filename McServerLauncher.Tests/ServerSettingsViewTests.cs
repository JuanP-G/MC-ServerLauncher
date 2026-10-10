using System.Text.Json;
using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// A server's settings page and the draft behind it, with real controls.
/// </summary>
/// <remarks>
/// <para>
/// The page replaced an edit dialog that bound straight to the live config and put a snapshot back
/// on Cancel. A page in the window is not modal, so the controls edit a copy; what has to hold is
/// that the copy is what the controls show, that nothing reaches the server before Save, and that
/// Save takes back only what was changed.
/// </para>
/// <para>
/// None of it is reachable without real controls — a unit test sees the model agreeing with itself
/// and passes — which is why these run under <see cref="AvaloniaFixture"/>. The page is built and
/// not shown, as the appearance tests explain.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ServerSettingsViewTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-settings-" + Guid.NewGuid().ToString("N"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ServerSettingsView Open(ServerConfig config, ServerSettingsPage page = ServerSettingsPage.Game)
    {
        var view = new ServerSettingsView(new ServerViewModel(config), page);
        AvaloniaFixture.Pump();
        return view;
    }

    private static T Named<T>(ServerSettingsView view, ServerSettingsPage page, string name) where T : Control =>
        view.Section(page).FindControl<T>(name) ?? throw new InvalidOperationException($"no hay un control llamado {name}");

    [Fact]
    public void OpeningChangesNothing() =>
        ui.Run(() =>
        {
            // Every page made, so every one of them has read its values in: none of that may count
            // as a change, or the save bar would be up before anybody touched anything.
            var folder = Folder("abrir");
            File.WriteAllText(Path.Combine(folder, "server.properties"), "pvp=TRUE\ngamemode=Creative\nmotd=§6Hola\n");
            var view = Open(new ServerConfig { Name = "abrir", FolderPath = folder, Type = ServerType.Paper });

            foreach (var page in Enum.GetValues<ServerSettingsPage>()) view.Section(page);
            AvaloniaFixture.Pump();

            Assert.False(view.IsDirty);
        });

    [Fact]
    public void TypingGoesIntoTheDraftAndNotIntoTheServer() =>
        ui.Run(() =>
        {
            var config = new ServerConfig { Name = "viejo", FolderPath = Folder("uno") };
            var view = Open(config, ServerSettingsPage.Appearance);

            Named<TextBox>(view, ServerSettingsPage.Appearance, "NameBox").Text = "escrito a mano";
            AvaloniaFixture.Pump();

            Assert.Equal("viejo", config.Name);
            Assert.True(view.IsDirty);
            Assert.True(view.Draft.IsPageDirty(ServerSettingsPage.Appearance));
            Assert.False(view.Draft.IsPageDirty(ServerSettingsPage.Game));
        });

    [Fact]
    public void SavingWritesTheServerAndTheFile() =>
        ui.Run(() =>
        {
            var folder = Folder("dos");
            File.WriteAllText(Path.Combine(folder, "server.properties"), "difficulty=easy\nmax-players=8\n");
            var config = new ServerConfig { Name = "dos", FolderPath = folder, MaxRamGb = 4 };
            var view = Open(config);

            Named<ComboBox>(view, ServerSettingsPage.Game, "DifficultyBox").SelectedIndex = 3; // hard
            Named<NumericUpDown>(view, ServerSettingsPage.Performance, "MaxRamBox").Value = 6;
            AvaloniaFixture.Pump();

            Assert.True(view.Save());

            Assert.Equal(6, config.MaxRamGb);
            var text = File.ReadAllText(Path.Combine(folder, "server.properties"));
            Assert.Contains("difficulty=hard", text);
            Assert.Contains("max-players=8", text);    // the rest of the file is untouched
            Assert.False(view.IsDirty);
        });

    [Fact]
    public void DiscardingPutsTheControlsBackAndWritesNothing() =>
        ui.Run(() =>
        {
            var folder = Folder("tres");
            var config = new ServerConfig { Name = "original", FolderPath = folder };
            var view = Open(config, ServerSettingsPage.Appearance);
            var nameBox = Named<TextBox>(view, ServerSettingsPage.Appearance, "NameBox");
            var pvp = Named<ToggleSwitch>(view, ServerSettingsPage.Game, "PvpToggle");

            nameBox.Text = "a medio escribir";
            pvp.IsChecked = false;
            AvaloniaFixture.Pump();
            Assert.True(view.IsDirty);

            view.Discard();
            AvaloniaFixture.Pump();

            Assert.False(view.IsDirty);
            Assert.Equal("original", nameBox.Text);
            Assert.True(pvp.IsChecked);
            Assert.Equal("original", config.Name);
            Assert.False(File.Exists(Path.Combine(folder, "server.properties")));
        });

    [Fact]
    public void SavingKeepsWhatChangedOnTheServerMeanwhile() =>
        ui.Run(() =>
        {
            // The live config is written while the page is open — a loader install, the seed read
            // from the console. Copying the whole draft back would put the old values over them.
            var config = new ServerConfig { Name = "cuatro", FolderPath = Folder("cuatro"), JarFile = "server.jar", MinRamGb = 2 };
            var view = Open(config, ServerSettingsPage.Performance);

            Named<NumericUpDown>(view, ServerSettingsPage.Performance, "MinRamBox").Value = 3;
            config.JarFile = "paper.jar";
            config.LastKnownSeed = 42;
            AvaloniaFixture.Pump();

            Assert.True(view.Save());

            Assert.Equal(3, config.MinRamGb);
            Assert.Equal("paper.jar", config.JarFile);
            Assert.Equal(42, config.LastKnownSeed);
        });

    [Fact]
    public void AnOptionTheTypeCannotDoIsOffOnScreenWithoutCountingAsAChange() =>
        ui.Run(() =>
        {
            // The edit dialog's bug, kept fixed: the flag was cleared while the checkbox stayed
            // ticked. And the new way to get it wrong: clearing it as an edit would put the save
            // bar up on a page nobody has touched.
            var config = new ServerConfig
            {
                Name = "convertido", FolderPath = Folder("cinco"),
                Type = ServerType.Paper,        // no Hydraulic, no mod content
                BedrockModContentEnabled = true // as if it had been a Fabric server until a moment ago
            };
            var view = Open(config, ServerSettingsPage.Crossplay);
            var hydraulic = Named<ToggleSwitch>(view, ServerSettingsPage.Crossplay, "HydraulicCheck");

            Assert.False(hydraulic.IsEnabled);
            Assert.False(hydraulic.IsChecked);
            Assert.False(view.IsDirty);
        });

    [Fact]
    public void ALoaderInstallIsTakenAsSavedAndRefreshesWhatDependsOnTheType() =>
        ui.Run(() =>
        {
            var config = new ServerConfig { Name = "seis", FolderPath = Folder("seis"), Type = ServerType.Vanilla };
            var view = Open(config, ServerSettingsPage.Crossplay);
            var crossplay = Named<ToggleSwitch>(view, ServerSettingsPage.Crossplay, "CrossplayCheck");
            Assert.False(crossplay.IsEnabled);    // Vanilla takes no plugins

            // What LoaderSection does once the files are on disk.
            config.Type = ServerType.Paper;
            config.JarFile = "paper.jar";
            view.Draft.Adopt(LoaderSection.InstalledFields);
            ((IServerSettingsSection)view.Section(ServerSettingsPage.Crossplay)).Reload();

            Assert.False(view.IsDirty);
            Assert.Equal("paper.jar", view.Draft.Config.JarFile);
            Assert.True(crossplay.IsEnabled);
        });

    [Fact]
    public void AnEmptyNameIsRefusedOnItsPage() =>
        ui.Run(() =>
        {
            var config = new ServerConfig { Name = "siete", FolderPath = Folder("siete") };
            var view = Open(config, ServerSettingsPage.Game);
            Named<TextBox>(view, ServerSettingsPage.Appearance, "NameBox").Text = "   ";
            AvaloniaFixture.Pump();

            Assert.False(view.Save());

            Assert.Equal(ServerSettingsPage.Appearance, view.Page);
            Assert.True(Named<TextBlock>(view, ServerSettingsPage.Appearance, "Error").IsVisible);
            Assert.Equal("siete", config.Name);
        });

    [Fact]
    public void TheFolderIsOnlyCheckedWhenItWasChanged() =>
        ui.Run(() =>
        {
            // A server whose folder has gone missing must still be able to change anything else.
            var gone = Path.Combine(_root, "no-existe");
            var config = new ServerConfig { Name = "ocho", FolderPath = gone, MaxRamGb = 4 };
            var view = Open(config, ServerSettingsPage.Performance);

            Named<NumericUpDown>(view, ServerSettingsPage.Performance, "MaxRamBox").Value = 5;
            AvaloniaFixture.Pump();
            Assert.True(view.Save());

            Named<TextBox>(view, ServerSettingsPage.Advanced, "FolderBox").Text = Path.Combine(_root, "tampoco");
            AvaloniaFixture.Pump();
            Assert.False(view.Save());
            Assert.Equal(ServerSettingsPage.Advanced, view.Page);
        });

    [Fact]
    public void LeavingWithSomethingUnsavedIsRefused() =>
        ui.Run(() =>
        {
            var view = Open(new ServerConfig { Name = "nueve", FolderPath = Folder("nueve") });
            Assert.True(view.TryLeave());

            Named<ToggleSwitch>(view, ServerSettingsPage.Game, "HardcoreToggle").IsChecked = true;
            AvaloniaFixture.Pump();

            Assert.False(view.TryLeave());
            Assert.Contains("nudged", view.FindControl<Border>("SaveBar")!.Classes);
        });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>The settings page against the server list and the rest of the window.</summary>
/// <remarks>
/// A real <see cref="MainViewModel"/> over a temporary data folder, without <c>Activate()</c>, as in
/// <see cref="MainViewModelFlowTests"/>: nothing here reaches outside the app.
/// </remarks>
[Collection("avalonia")]
public class ServerSettingsFlowTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mcl-sflow-" + Guid.NewGuid().ToString("N"));

    public ServerSettingsFlowTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dataDir);
        var servers = new[] { "uno", "dos" }.Select(name => new ServerConfig
        {
            Id = name, Name = name, FolderPath = Directory.CreateDirectory(Path.Combine(_dataDir, name)).FullName,
            Type = ServerType.Vanilla, GameVersion = "1.21.1"
        });
        File.WriteAllText(Path.Combine(_dataDir, "servers.json"), JsonSerializer.Serialize(servers));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    private static void Touch(MainViewModel main)
    {
        main.ServerSettingsPanel!.Section(ServerSettingsPage.Game)
            .FindControl<ToggleSwitch>("HardcoreToggle")!.IsChecked = true;
        AvaloniaFixture.Pump();
    }

    [Fact]
    public void TheCardsPencilAndConfigureOpenTheSamePage() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);

            main.EditAppearanceCommand.Execute(null);
            var panel = main.ServerSettingsPanel;
            Assert.NotNull(panel);
            Assert.Equal(ServerSettingsPage.Appearance, panel!.Page);
            Assert.True(main.IsEditingServer);
            Assert.False(main.ShowServerDetail);

            main.ConfigureServerCommand.Execute(null);
            Assert.Same(panel, main.ServerSettingsPanel);
        });

    [Fact]
    public void PickingAnotherServerLeavesCleanSettings() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.ConfigureServerCommand.Execute(null);

            main.SelectedServer = main.Servers[1];

            Assert.Null(main.ServerSettingsPanel);
            Assert.True(main.ShowServerDetail);
        });

    [Fact]
    public void PickingAnotherServerWithSomethingUnsavedGoesBack() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            var first = main.SelectedServer;
            main.ConfigureServerCommand.Execute(null);
            Touch(main);

            main.SelectedServer = main.Servers[1];
            AvaloniaFixture.Pump();

            Assert.Same(first, main.SelectedServer);
            Assert.NotNull(main.ServerSettingsPanel);
            Assert.Contains("nudged", main.ServerSettingsPanel!.FindControl<Border>("SaveBar")!.Classes);
        });

    [Fact]
    public void AnotherSectionIsRefusedWithSomethingUnsavedAndAllowedWithout() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.ConfigureServerCommand.Execute(null);
            Touch(main);

            main.ShowAboutCommand.Execute(null);
            Assert.True(main.IsServersSection);

            main.ServerSettingsPanel!.Discard();
            main.ShowAboutCommand.Execute(null);
            Assert.True(main.IsAboutSection);
            Assert.NotNull(main.ServerSettingsPanel);   // kept: nothing was lost by looking away
        });

    [Fact]
    public void ClosingTheWindowIsHeldOnceForUnsavedSettings() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            Assert.False(main.HoldCloseForUnsavedSettings());

            main.ConfigureServerCommand.Execute(null);
            Touch(main);
            main.ShowServersCommand.Execute(null);

            Assert.True(main.HoldCloseForUnsavedSettings());
            Assert.True(main.IsServersSection);
        });

    [Fact]
    public void ARenameIsSavedToServersJson() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.EditAppearanceCommand.Execute(null);
            main.ServerSettingsPanel!.Section(ServerSettingsPage.Appearance)
                .FindControl<TextBox>("NameBox")!.Text = "renombrado";
            AvaloniaFixture.Pump();

            Assert.True(main.ServerSettingsPanel.Save());
            AvaloniaFixture.Pump();

            Assert.Equal("renombrado", main.SelectedServer!.Name);
            Assert.Contains("renombrado", File.ReadAllText(Path.Combine(_dataDir, "servers.json")));
        });
}
