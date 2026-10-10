using System.Reflection;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A start waits for a backup that is still reading the world, instead of starting beside it.
/// </summary>
/// <remarks>
/// Pressing Start right after Stop found the stop's own backup still zipping. The start's backup was
/// refused as "already running" and the server started anyway, writing to the world being copied.
/// </remarks>
[Collection("avalonia")]
public class StartWaitsForBackupTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-gate-" + Guid.NewGuid().ToString("N"));

    private (ServerViewModel Server, SemaphoreSlim Gate) Server()
    {
        Directory.CreateDirectory(_folder);
        ServerViewModel? server = null;
        ui.Run(() => server = new ServerViewModel(new ServerConfig { Name = "survival", FolderPath = _folder }));
        var gate = (SemaphoreSlim)typeof(ServerViewModel)
            .GetField("_backupGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        return (server!, gate);
    }

    [Fact]
    public async Task TheStartsBackupWaitsForTheOneInProgress()
    {
        var (server, gate) = Server();
        await gate.WaitAsync();                               // the stop's backup, still running

        var start = server.RunBackupAsync("start", waitForOthers: true);
        await Task.Delay(200);
        Assert.False(start.IsCompleted);                      // waiting, not refused

        gate.Release();                                       // the stop's backup finishes
        await start.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnyOtherBackupIsStillRefusedRatherThanQueued()
    {
        var (server, gate) = Server();
        await gate.WaitAsync();

        Assert.Null(await server.RunBackupAsync("manual").WaitAsync(TimeSpan.FromSeconds(5)));
        gate.Release();
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
