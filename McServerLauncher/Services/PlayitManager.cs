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

    /// <summary>
    /// Whether the one-off Linux probe (which unit exists, is the binary on PATH) has already run.
    /// </summary>
    /// <remarks>
    /// A separate flag is needed because the answer may legitimately be "none", and
    /// <c>_linuxUnit ??= …</c> cannot remember that: on a machine without Playit — every Linux user
    /// who doesn't use it — the probe re-ran on every tick, spawning three <c>systemctl status</c>
    /// plus a <c>which</c>, each blocking up to five seconds, on the UI thread.
    /// </remarks>
    private bool _linuxProbed;

    /// <summary>Result of the one-off <c>which playit</c>, kept so it isn't asked again each tick.</summary>
    private bool _linuxBinaryOnPath;

    /// <summary>Serialises probes: several view models share this instance and a probe spawns processes.</summary>
    private readonly SemaphoreSlim _probeGate = new(1, 1);

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
    public void RefreshState(bool force = false) => _ = RefreshStateAsync(force);

    /// <summary>
    /// Same as <see cref="RefreshState"/>, awaitable — for callers that need the state updated
    /// before they continue (starting or stopping the service).
    /// </summary>
    public async Task RefreshStateAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(2))
            return;

        // A timer-driven refresh is droppable: if a probe is already running its result is about to
        // arrive anyway, and piling up probes is the cost this method exists to avoid. A forced one
        // is NOT droppable — it follows a start/stop and its whole job is to observe the new state,
        // so it waits its turn and always probes.
        if (force)
            await _probeGate.WaitAsync();
        else if (!await _probeGate.WaitAsync(0))
            return;

        try
        {
            _lastRefresh = DateTime.UtcNow;

            // A forced refresh follows an install/start/stop, so anything the one-off probe
            // concluded may no longer hold — ask again.
            if (force) _linuxProbed = false;

            // Off the dispatcher: querying the agent runs external commands that block for up to
            // five seconds each, and this is called from a 3 s UI timer.
            await Task.Run(Probe);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private void Probe()
    {
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
        // The expensive questions — which unit exists, is the binary on PATH — are asked once.
        // Neither answer changes while the app runs, short of the user installing Playit, which a
        // forced refresh re-probes for.
        if (!_linuxProbed)
        {
            _linuxProbed = true;
            _linuxUnit = LinuxUnitNames.FirstOrDefault(UnitExists);
            _linuxBinaryOnPath = _linuxUnit is null && Run("which", "playit").ExitCode == 0;
        }

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

        // No systemd unit: the only thing worth re-checking each tick is whether the process is
        // alive, and that reads the process table without starting anything.
        var running = Process.GetProcessesByName("playit").Length > 0;
        IsInstalled = running || _linuxBinaryOnPath;
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
        await RefreshStateAsync(force: true);
    }

    /// <summary>Stops the Playit agent (Windows service / Linux systemd unit). May require privileges.</summary>
    public async Task StopServiceAsync()
    {
        if (OperatingSystem.IsWindows())
            await Task.Run(StopWindows);
        else if (OperatingSystem.IsLinux() && _linuxUnit is not null)
            await Task.Run(() => Systemctl("stop", _linuxUnit));
        await RefreshStateAsync(force: true);
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
