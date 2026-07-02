using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Playit.gg integration that relies on the background Playit agent already keeping the tunnels
/// up 24/7. The app does not launch its own playit binary: it only queries and, when possible,
/// starts/stops the agent.
/// - On Windows the agent is a Windows service ("playitd"), managed via <see cref="ServiceController"/>.
/// - On Linux it is typically a systemd unit, managed via <c>systemctl</c> (start/stop may need privileges).
/// </summary>
public class PlayitManager
{
    /// <summary>
    /// Shared instance: the agent is machine-wide state, so every ServerViewModel polls this one
    /// instead of creating its own (avoids N status queries per tick with N servers).
    /// </summary>
    public static PlayitManager Shared { get; } = new();

    private const string WindowsServiceName = "playitd";
    private DateTime _lastRefresh = DateTime.MinValue;

    // Likely systemd unit names for the Playit agent on Linux.
    private static readonly string[] LinuxUnitNames = { "playit", "playit-agent", "playitd" };
    private string? _linuxUnit;

    public event Action<PlayitState>? StateChanged;

    private PlayitState _state = PlayitState.Stopped;
    public PlayitState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>True if the Playit agent is installed on the system.</summary>
    public bool IsInstalled { get; private set; }

    public bool IsRunning => State == PlayitState.Running;

    /// <summary>
    /// Queries the current agent status and updates <see cref="State"/>. Calls are throttled
    /// (several view models poll the shared instance); pass <paramref name="force"/> to bypass
    /// the throttle, e.g. right after starting/stopping the service.
    /// </summary>
    public void RefreshState(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(2))
            return;
        _lastRefresh = DateTime.UtcNow;

        if (OperatingSystem.IsWindows())
            RefreshWindows();
        else if (OperatingSystem.IsLinux())
            RefreshLinux();
        else
        {
            IsInstalled = false;
            State = PlayitState.Stopped;
        }
    }

    [SupportedOSPlatform("windows")]
    private void RefreshWindows()
    {
        try
        {
            using var sc = new ServiceController(WindowsServiceName);
            var status = sc.Status; // throws if the service does not exist
            IsInstalled = true;
            State = status switch
            {
                ServiceControllerStatus.Running => PlayitState.Running,
                ServiceControllerStatus.StartPending => PlayitState.Starting,
                _ => PlayitState.Stopped
            };
        }
        catch
        {
            IsInstalled = false;
            State = PlayitState.Stopped;
        }
    }

    private void RefreshLinux()
    {
        // Find a known systemd unit the first time.
        _linuxUnit ??= LinuxUnitNames.FirstOrDefault(UnitExists);

        if (_linuxUnit is not null)
        {
            IsInstalled = true;
            var active = Run("systemctl", $"is-active {_linuxUnit}").Output.Trim();
            State = active switch
            {
                "active" => PlayitState.Running,
                "activating" => PlayitState.Starting,
                _ => PlayitState.Stopped
            };
            return;
        }

        // No systemd unit: fall back to detecting a running 'playit' process or the binary on PATH.
        var running = Process.GetProcessesByName("playit").Length > 0;
        IsInstalled = running || Run("which", "playit").ExitCode == 0;
        State = running ? PlayitState.Running : PlayitState.Stopped;
    }

    private static bool UnitExists(string unit)
    {
        // 'systemctl status' exits 0 (running), 3 (stopped) when the unit is known; 4 when unknown.
        var code = Run("systemctl", $"status {unit}").ExitCode;
        return code is 0 or 1 or 2 or 3;
    }

    /// <summary>Starts the Playit agent (Windows service / Linux systemd unit). May require privileges.</summary>
    public async Task StartServiceAsync()
    {
        if (OperatingSystem.IsWindows())
            await Task.Run(StartWindows);
        else if (OperatingSystem.IsLinux() && _linuxUnit is not null)
            await Task.Run(() => Systemctl("start", _linuxUnit));
        RefreshState(force: true);
    }

    /// <summary>Stops the Playit agent (Windows service / Linux systemd unit). May require privileges.</summary>
    public async Task StopServiceAsync()
    {
        if (OperatingSystem.IsWindows())
            await Task.Run(StopWindows);
        else if (OperatingSystem.IsLinux() && _linuxUnit is not null)
            await Task.Run(() => Systemctl("stop", _linuxUnit));
        RefreshState(force: true);
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindows()
    {
        using var sc = new ServiceController(WindowsServiceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StopWindows()
    {
        using var sc = new ServiceController(WindowsServiceName);
        if (sc.CanStop && sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }
    }

    private static void Systemctl(string action, string unit)
    {
        var r = Run("systemctl", $"{action} {unit}");
        if (r.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
    }

    /// <summary>Runs a command and returns its exit code and captured output. Never throws.</summary>
    private static (int ExitCode, string Output, string Error) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return (-1, string.Empty, string.Empty);
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.HasExited ? p.ExitCode : -1, output, error);
        }
        catch
        {
            return (-1, string.Empty, string.Empty);
        }
    }
}
