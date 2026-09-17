using System.Text.Json;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Building a view model without starting it.
/// </summary>
/// <remarks>
/// <para>
/// Neither <see cref="ServerViewModel"/> nor <see cref="MainViewModel"/> could be reached from a
/// test before: their constructors started polling timers, subscribed to the process-wide Playit
/// singletons, opened the wake-on-demand socket, went to the network and — for the main one — read
/// and rewrote the real <c>servers.json</c> of whoever was running the test. Everything that
/// touches them was therefore tested through its pure pieces, or not at all.
/// </para>
/// <para>
/// Now a constructor assembles and <c>Activate()</c> starts. These tests hold that line: if
/// something that reaches outside the app creeps back into a constructor, the run either hangs, or
/// fails here, or starts probing a service that does not exist on the Linux leg of CI.
/// </para>
/// <para>
/// <c>Activate()</c> itself is deliberately never called. It is the half that talks to Playit and
/// the network, and a test that exercised it would be testing someone else's machine.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ViewModelLifecycleTests : IDisposable
{
    private readonly AvaloniaFixture _ui;

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "mcl-life-" + Guid.NewGuid().ToString("N"));

    public ViewModelLifecycleTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>A server folder with nothing in it, which is the honest starting state.</summary>
    private string ServerFolder(string name)
    {
        var path = Path.Combine(_dataDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteServersJson(params ServerConfig[] servers) =>
        File.WriteAllText(
            Path.Combine(_dataDir, "servers.json"),
            JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }));

    [Fact]
    public void AServerCanBeBuiltWithoutStartingAnything()
    {
        _ui.Run(() =>
        {
            var config = new ServerConfig
            {
                Name = "survival", FolderPath = ServerFolder("survival"),
                Type = ServerType.Fabric, GameVersion = "1.21.1"
            };

            var server = new ServerViewModel(config);

            // The panels exist and have read the folder: building is allowed to look at disk.
            Assert.NotNull(server.Mods);
            Assert.NotNull(server.Backups);
            Assert.Equal("survival", server.Name);
            Assert.True(server.IsModded);
        });
    }

    [Fact]
    public void ShuttingDownOneThatWasNeverStartedIsHarmless()
    {
        // The normal case in a test, and a real one in the app: closing the window while a server
        // is still being registered. Unsubscribing a handler that was never subscribed would take
        // a different view model's with it, since delegates compare by target and method.
        _ui.Run(() =>
        {
            var server = new ServerViewModel(new ServerConfig
            {
                Name = "never-started", FolderPath = ServerFolder("never-started")
            });

            // On the UI thread and waited for here rather than awaited: with no process running,
            // ShutdownAsync never actually yields, and the timers it stops belong to this thread.
            server.ShutdownAsync().GetAwaiter().GetResult();
            server.ShutdownAsync().GetAwaiter().GetResult();   // the tray path can ask twice
        });
    }

    [Fact]
    public void TheMainViewModelReadsTheFolderItIsGiven()
    {
        // The whole point of the injected folder: this must be the temp directory's server list,
        // not the one belonging to whoever is running the test.
        WriteServersJson(
            new ServerConfig { Name = "uno", FolderPath = ServerFolder("uno"), GameVersion = "1.21.1" },
            new ServerConfig { Name = "dos", FolderPath = ServerFolder("dos"), GameVersion = "1.21.1" });

        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);

            Assert.Equal(new[] { "uno", "dos" }, main.Servers.Select(s => s.Name).ToArray());
            Assert.Equal("uno", main.SelectedServer?.Name);
        });
    }

    [Fact]
    public void AnEmptyFolderOpensWithNoServersAndNoComplaints()
    {
        _ui.Run(() =>
        {
            var main = new MainViewModel(_dataDir);

            Assert.Empty(main.Servers);
            Assert.Null(main.SelectedServer);
            Assert.False(main.HasSelection);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }
}
