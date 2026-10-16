using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>A server as <see cref="BackupCoordinator"/> sees it: a console, a process, a few flags.</summary>
internal sealed class FakeBackupHost : IBackupHost
{
    public FakeBackupHost(string folder) => Config = new ServerConfig { Name = "survival", FolderPath = folder };

    public ServerConfig Config { get; }
    public bool IsProcessRunning { get; set; }
    public bool IsPreparing { get; set; }
    public int PlayersOnline { get; set; }
    public List<string> Commands { get; } = new();
    public List<string> Lines { get; } = new();

    public void SendToConsole(string command)
    {
        lock (Commands) Commands.Add(command);
    }

    public void Say(string line)
    {
        lock (Lines) Lines.Add(line);
    }

    /// <summary>What a stop does: "stop" in the console, and the JVM gone once it returns.</summary>
    public Task Stop(Action? onTheWayDown = null)
    {
        SendToConsole("stop");
        onTheWayDown?.Invoke();
        IsProcessRunning = false;
        return Task.CompletedTask;
    }
}
