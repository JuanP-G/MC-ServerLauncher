using System.Net;
using System.Net.Sockets;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A real Pumpkin, started and stopped by the app's own process manager.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: it needs the 110 MB executable, which is neither in the repository nor something a test
/// run should download. Point <c>MCSL_PUMPKIN_EXE</c> at one —
/// <c>pumpkin-X64-Windows.exe</c> from a Pumpkin release — and it runs; without it, it passes
/// having checked nothing, and says so in its name.
/// </para>
/// <para>
/// Everything else about Pumpkin is tested from lines captured once. This is the test that fails
/// when a new Pumpkin release changes those lines, which is the day they need capturing again.
/// </para>
/// </remarks>
public class PumpkinRealServerTests
{
    private static string? Executable =>
        Environment.GetEnvironmentVariable("MCSL_PUMPKIN_EXE") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;

    [Fact]
    public async Task StartsIsSeenReadyAnswersAndStops_WhenAnExecutableIsGiven()
    {
        if (Executable is not { } source) return;

        var folder = Path.Combine(Path.GetTempPath(), "mcsl-pumpkin-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var name = PumpkinService.ExecutableName;
        File.Copy(source, Path.Combine(folder, name));
        FileModes.TryMakeExecutable(Path.Combine(folder, name));

        // The file a new server gets, on a port nothing else is using.
        var port = FreePort();
        Assert.True(PumpkinConfig.WriteInitial(folder, port, "Prueba - MC Server Launcher", seed: "mcsl"));

        var config = new ServerConfig
        {
            Name = "Prueba",
            FolderPath = folder,
            JarFile = name,
            Type = ServerType.Pumpkin
        };
        // What the rest of the app would be told: the port, before the server has ever run.
        Assert.Equal(port, new ServerPropertiesService().GetServerPort(config.PropertiesPath));

        var lines = new List<string>();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ServerProcessManager();
        manager.OutputReceived += (line, _) => { lock (lines) lines.Add(line); };
        manager.StateChanged += state => { if (state == ServerState.Running) running.TrySetResult(); };

        try
        {
            manager.Start(config);
            await running.Task.WaitAsync(TimeSpan.FromMinutes(2));

            // Ready, and in the format the player list can be trusted in.
            Assert.True(manager.PlayerTrackingReliable);
            Assert.Contains(Snapshot(lines), l => l.Contains("]: Started server; took ", StringComparison.Ordinal));

            manager.SendCommand("list");
            await WaitForAsync(lines, l => l.StartsWith("There are 0 of a max of ", StringComparison.Ordinal));

            manager.SendCommand("save-all");
            await WaitForAsync(lines, SaveConfirmation.IsSaveFinished);

            await manager.StopAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(ServerState.Stopped, manager.State);

            // A clean stop, not the kill after the timeout.
            Assert.Contains(Snapshot(lines), l => l.Contains("]: The server has stopped.", StringComparison.Ordinal));

            // And it left what the app expects to find: the world, and the lists under data/.
            Assert.True(ServerCreationService.WorldExists(folder));
            Assert.True(File.Exists(PlayerFiles.PathOf(folder, "ops.json")));
            Assert.Equal(port, new ServerPropertiesService().GetServerPort(config.PropertiesPath));
        }
        finally
        {
            if (manager.IsRunning) await manager.StopAsync(TimeSpan.FromSeconds(10));
            try { Directory.Delete(folder, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// The whole of "create a server": the release looked up on GitHub, the executable downloaded
    /// and checked against the release's own checksums, the first <c>pumpkin.toml</c> written, and
    /// the result started and stopped.
    /// </summary>
    /// <remarks>
    /// Opt-in twice over, with <c>MCSL_PUMPKIN_DOWNLOAD=1</c>: it downloads over a hundred megabytes
    /// from the network, which no ordinary test run should.
    /// </remarks>
    [Fact]
    public async Task IsCreatedFromTheLatestReleaseAndRuns_WhenDownloadingIsAllowed()
    {
        if (Environment.GetEnvironmentVariable("MCSL_PUMPKIN_DOWNLOAD") != "1") return;

        var folder = Path.Combine(Path.GetTempPath(), "mcsl-pumpkin-create-" + Guid.NewGuid().ToString("N"));
        var port = FreePort();
        var log = new List<string>();
        using var manager = new ServerProcessManager();

        try
        {
            var config = await new ServerProvisioningService().CreateAsync(new NewServerPlan(
                Name: "Creado", Folder: folder, Type: ServerType.Pumpkin,
                Version: new MinecraftVersion { Id = string.Empty, Type = "release" },
                Memory: MemoryMode.Automatic, MinRamGb: 2, MaxRamGb: 4, Port: port, Seed: null,
                MultiVersion: false, Hydraulic: false, Playit: false, Crossplay: false),
                line => { lock (log) log.Add(line); });

            Assert.Equal(ServerType.Pumpkin, config.Type);
            Assert.Equal(PumpkinService.ExecutableName, config.JarFile);
            Assert.True(File.Exists(config.JarFullPath));
            Assert.False(string.IsNullOrEmpty(config.GameVersion));       // from the release's tag
            Assert.False(string.IsNullOrEmpty(config.ModLoaderVersion));
            Assert.False(config.CrossplayEnabled);

            // Nothing a Java server would have been given.
            Assert.False(File.Exists(Path.Combine(folder, "eula.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "server.properties")));
            Assert.False(File.Exists(Path.Combine(folder, "run.bat")));
            Assert.Equal(port, new ServerPropertiesService().GetServerPort(config.PropertiesPath));

            var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.StateChanged += state => { if (state == ServerState.Running) running.TrySetResult(); };
            manager.Start(config);
            await running.Task.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(manager.PlayerTrackingReliable);

            // What the idle shutdown asks before it stops an empty server: Pumpkin answers the
            // Java status ping, with a player count in it.
            Assert.Equal(0, await ConnectionProbe.PlayersOnlineAsync(null, port, TimeSpan.FromSeconds(10)));

            await manager.StopAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(ServerState.Stopped, manager.State);
        }
        finally
        {
            if (manager.IsRunning) await manager.StopAsync(TimeSpan.FromSeconds(10));
            try { Directory.Delete(folder, recursive: true); } catch { /* temp */ }
        }
    }

    private static List<string> Snapshot(List<string> lines)
    {
        lock (lines) return [.. lines];
    }

    private static async Task WaitForAsync(List<string> lines, Func<string, bool> wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Snapshot(lines).Any(wanted)) return;
            await Task.Delay(100);
        }
        Assert.Fail("The console never showed the expected line. It showed:\n" + string.Join('\n', Snapshot(lines)));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
