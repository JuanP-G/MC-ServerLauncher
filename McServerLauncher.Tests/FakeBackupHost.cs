using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>A server as <see cref="BackupCoordinator"/> sees it: a console, a process, a few flags.</summary>
internal sealed class FakeBackupHost : IBackupHost
{
    public FakeBackupHost(string folder) => Config = new ServerConfig
    {
        Name = "survival",
        FolderPath = folder
    };

    public ServerConfig Config { get; }
    public bool IsProcessRunning { get; set; }
    public bool IsPreparing { get; set; }
    public int PlayersOnline { get; set; }
    private readonly List<string> _commands = new();
    private readonly List<string> _lines = new();

    /// <summary>The commands sent so far. A copy: the host is written to from the coordinator's threads.</summary>
    public List<string> Commands { get { lock (_commands) return _commands.ToList(); } }

    /// <summary>The lines said so far. A copy, for the same reason: enumerating the live list while a
    /// copy of the world is still reporting progress threw "Collection was modified" now and then.</summary>
    public List<string> Lines { get { lock (_lines) return _lines.ToList(); } }

    public void SendToConsole(string command)
    {
        lock (_commands) _commands.Add(command);
    }

    public void Say(string line)
    {
        lock (_lines) _lines.Add(line);
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
