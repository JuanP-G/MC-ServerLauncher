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
            Name = "Prueba", FolderPath = folder, JarFile = name, Type = ServerType.Pumpkin
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
