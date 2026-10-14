using System.Text.Json;
using Avalonia.Controls;
using McServerLauncher.Localization;
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

    [Fact]
    public void ANumberedDifficultyAndGameModeShowAsWhatTheyAre() =>
        ui.Run(() =>
        {
            // Minecraft still takes the old ids; the drop-downs showed their first item for them,
            // so a hard creative server read as peaceful survival.
            var folder = Folder("dieciseis");
            File.WriteAllText(Path.Combine(folder, "server.properties"), "difficulty=3\ngamemode=1\n");
            var view = Open(new ServerConfig { Name = "dieciseis", FolderPath = folder });

            Assert.Equal("hard", Named<ComboBox>(view, ServerSettingsPage.Game, "DifficultyBox").SelectedItem
                is ComboBoxItem { Tag: string d } ? d : null);
            Assert.Equal("creative", Named<ComboBox>(view, ServerSettingsPage.Game, "GamemodeBox").SelectedItem
                is ComboBoxItem { Tag: string g } ? g : null);
            Assert.False(view.IsDirty);   // shown as what it is, not rewritten
        });

    [Fact]
    public void AValueTheListDoesNotKnowShowsNothingPicked() =>
        ui.Run(() =>
        {
            var folder = Folder("diecisiete");
            File.WriteAllText(Path.Combine(folder, "server.properties"), "gamemode=minigame\n");
            var view = Open(new ServerConfig { Name = "diecisiete", FolderPath = folder });

            Assert.Null(Named<ComboBox>(view, ServerSettingsPage.Game, "GamemodeBox").SelectedItem);
            Assert.False(view.IsDirty);
        });

    [Fact]
    public void ASwitchTheAppTurnsBackOffIsShownOff() =>
        ui.Run(() =>
        {
            // Multi-version is switched on and saved; its install fails and the app switches it back
            // off on the server. The page went on showing it on, and saving "on" again was no change,
            // so nothing tried the install again.
            var config = new ServerConfig { Name = "trece", FolderPath = Folder("trece"), Type = ServerType.Paper };
            var view = Open(config, ServerSettingsPage.Crossplay);
            view.Draft.Config.MultiVersionEnabled = true;
            Assert.True(view.Save());

            config.MultiVersionEnabled = false;   // what a failed install does
            AvaloniaFixture.Pump();

            Assert.False(view.Draft.Config.MultiVersionEnabled);
            Assert.False(view.IsDirty);
            view.Draft.Config.MultiVersionEnabled = true;
            Assert.True(view.IsDirty);           // so saving it on again asks for the install again
        });

    [Fact]
    public void WhatTheUserChangedIsNotOverwrittenFromOutside() =>
        ui.Run(() =>
        {
            var config = new ServerConfig { Name = "catorce", FolderPath = Folder("catorce"), JavaPath = "java" };
            var view = Open(config, ServerSettingsPage.Performance);
            view.Draft.Config.JavaPath = "/opt/my-java/bin/java";

            config.JavaPath = "/usr/lib/jvm/21/bin/java";   // the app found a better Java meanwhile
            AvaloniaFixture.Pump();

            Assert.Equal("/opt/my-java/bin/java", view.Draft.Config.JavaPath);
        });

    [Fact]
    public void AClosedPageStopsFollowingItsServer() =>
        ui.Run(() =>
        {
            var config = new ServerConfig { Name = "quince", FolderPath = Folder("quince") };
            var view = Open(config);
            view.Detach();

            config.JavaPath = "/somewhere/else/java";

            Assert.NotEqual("/somewhere/else/java", view.Draft.Config.JavaPath);
        });

    [Fact]
    public void ALoaderCannotBeInstalledUnderARunningServer() =>
        ui.Run(() =>
        {
            // The page used to be a modal dialog; on the page nothing stopped an install from
            // replacing the jar of a server that was running.
            var server = new ServerViewModel(new ServerConfig { Name = "diez", FolderPath = Folder("diez") });
            server.State = ServerState.Running;
            var view = new ServerSettingsView(server, ServerSettingsPage.Loader);
            AvaloniaFixture.Pump();

            Assert.False(Named<Button>(view, ServerSettingsPage.Loader, "InstallButton").IsEnabled);
        });

    [Fact]
    public void NothingStartsTheServerWhileALoaderIsBeingInstalled() =>
        ui.Run(() =>
        {
            var server = new ServerViewModel(new ServerConfig { Name = "once", FolderPath = Folder("once") });
            Assert.True(server.StartCommand.CanExecute(null));

            server.IsConverting = true;

            Assert.False(server.CanStart);
            Assert.False(server.StartCommand.CanExecute(null));
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

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool Accepts(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void ChangingThePortMovesTheWakeListenerWithIt()
    {
        // The listener read the port once and kept it: after a change it went on answering on the
        // old one, and players knocking on the new one woke nothing until the server next stopped.
        var (before, after) = (FreePort(), FreePort());
        File.WriteAllText(Path.Combine(_dataDir, "uno", "server.properties"), $"server-port={before}");
        MainViewModel? main = null;

        _ui.Run(() =>
        {
            main = new MainViewModel(_dataDir);
            main.SelectedServer!.Config.WakeOnDemand = true;   // listening on the old port
            main.ConfigureServerCommand.Execute(null);
            main.ServerSettingsPanel!.Section(ServerSettingsPage.Network)
                .FindControl<NumericUpDown>("PortBox")!.Value = after;
            AvaloniaFixture.Pump();

            Assert.True(main.ServerSettingsPanel.Save());
            AvaloniaFixture.Pump();
        });

        try
        {
            Assert.True(Accepts(after), "nothing answers on the new port");
            Assert.False(Accepts(before), "the old port is still being answered");
        }
        finally
        {
            _ui.Run(() => main!.SelectedServer!.Config.WakeOnDemand = false);
        }
    }

    [Fact]
    public void AServersJsonThatCannotBeWrittenIsSaidOnTheBar() =>
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);
            main.EditAppearanceCommand.Execute(null);
            var panel = main.ServerSettingsPanel!;
            panel.Section(ServerSettingsPage.Appearance).FindControl<TextBox>("NameBox")!.Text = "renombrado";
            AvaloniaFixture.Pump();

            // servers.json cannot be replaced: a folder stands where it goes.
            var json = Path.Combine(_dataDir, "servers.json");
            File.Delete(json);
            Directory.CreateDirectory(json);

            Assert.True(panel.Save());   // the page itself saved
            AvaloniaFixture.Pump();

            Assert.True(panel.FindControl<Border>("SaveBar")!.IsVisible);
            Assert.Contains("error", panel.FindControl<TextBlock>("SaveBarHint")!.Classes);
            // Nothing is unsaved: the bar says the save did not finish, not that changes are pending.
            Assert.Equal(Localizer.Get("Sset_SaveUnfinished"), panel.FindControl<TextBlock>("SaveBarText")!.Text);
        });
}
