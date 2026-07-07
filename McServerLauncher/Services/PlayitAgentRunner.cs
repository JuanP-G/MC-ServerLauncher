using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>State of the embedded Playit agent process.</summary>
public enum AgentRunState { Stopped, Downloading, Starting, Running, Failed, Unsupported }

/// <summary>
/// Runs Playit's official agent (<c>playitd</c>) as a child process so the user's tunnels actually
/// forward traffic — without them installing anything. The binary is downloaded once (pinned to the
/// same version the app registers, <see cref="AgentVersion"/>) and launched with <c>--secret</c>,
/// the per-user agent key from the setup-code flow. One agent serves all of the user's tunnels; it
/// runs while the app is open and connected, and is stopped on shutdown. App-wide singleton.
/// </summary>
public class PlayitAgentRunner
{
    public static PlayitAgentRunner Shared { get; } = new();

    // Pinned to match the (variant_id, version) pair the app registers via create_agent.
    private const string AgentVersion = "v1.0.10";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly object _lock = new();
    private Process? _process;
    private string? _runningSecret;
    private bool _reachedRunning;
    private int _generation; // bumped on each start/stop so a replaced or intentionally-killed process is ignored
    private readonly List<string> _recentOutput = new();

    public AgentRunState State { get; private set; } = AgentRunState.Stopped;

    /// <summary>Human-readable reason for the last <see cref="AgentRunState.Failed"/> (for the UI).</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event Action<AgentRunState>? StateChanged;

    private void SetState(AgentRunState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private static string AgentDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "playit-agent");

    /// <summary>The release asset for this OS/arch, or null if Playit ships no binary for it (e.g. macOS).</summary>
    private static string? AssetName => RuntimeInformation.OSArchitecture switch
    {
        _ when OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64 => "playit-windows-x86_64-signed.exe",
        _ when OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X86 => "playit-windows-x86-signed.exe",
        Architecture.X64 when OperatingSystem.IsLinux() => "playit-linux-amd64",
        Architecture.Arm64 when OperatingSystem.IsLinux() => "playit-linux-aarch64",
        _ => null // macOS (no official binary) and other combos: not auto-runnable
    };

    /// <summary>True if the embedded agent can run on this platform.</summary>
    public bool PlatformSupported => AssetName is not null;

    /// <summary>Local path of the downloaded agent binary.</summary>
    private static string BinaryPath => Path.Combine(AgentDir, OperatingSystem.IsWindows() ? "playitd.exe" : "playitd");

    /// <summary>
    /// A private IPC socket/named pipe for our managed agent, so it never clashes with a system-wide
    /// Playit service the user may have installed (which uses playitd's default path and would make us
    /// fail with "Another instance is already running").
    /// </summary>
    private static string SocketPath => OperatingSystem.IsWindows()
        ? @"\\.\pipe\mc-server-launcher-playitd"
        : Path.Combine(AgentDir, "playitd.sock");

    /// <summary>
    /// Ensures the agent binary is present (downloading it once) and runs it with the given secret.
    /// No-op if already running with the same secret. Safe to call repeatedly.
    /// </summary>
    public async Task StartAsync(string secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretKey)) return;
        if (AssetName is null) { SetState(AgentRunState.Unsupported); return; }

        lock (_lock)
        {
            if (_process is { HasExited: false })
            {
                if (_runningSecret == secretKey) return; // already running with this secret
                TryKill();                                // secret changed (reconnect): restart
            }
        }

        int myGen;
        try
        {
            LastError = null;
            lock (_lock) { _reachedRunning = false; _recentOutput.Clear(); myGen = ++_generation; }
            var exe = await EnsureBinaryAsync();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--secret");
            psi.ArgumentList.Add(secretKey);
            psi.ArgumentList.Add("--socket-path");
            psi.ArgumentList.Add(SocketPath);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnAgentLine;
            proc.ErrorDataReceived += OnAgentLine;
            proc.Exited += (sender, _) =>
            {
                var exitCode = sender is Process p && p.HasExited ? p.ExitCode : (int?)null;
                bool startupFailure;
                lock (_lock)
                {
                    // A newer start (or Stop) bumped the generation: this process was replaced or
                    // killed on purpose — ignore its exit entirely.
                    if (myGen != _generation) return;
                    _process = null;
                    _runningSecret = null;
                    // Exiting before it ever came up = a startup failure (bad/expired secret, port,
                    // conflicting agent…).
                    startupFailure = !_reachedRunning;
                }
                if (!startupFailure) { SetState(AgentRunState.Stopped); return; }

                // Exited can fire before the last stdout/stderr callbacks do; give them a moment to
                // drain so we can surface playitd's own last words as the failure reason.
                Task.Delay(400).ContinueWith(t =>
                {
                    List<string> lines;
                    lock (_lock)
                    {
                        if (myGen != _generation) return; // a new start began meanwhile
                        lines = new List<string>(_recentOutput);
                    }
                    LastError = BuildFailureReason(lines, exitCode);
                    SetState(AgentRunState.Failed);
                });
            };

            lock (_lock)
            {
                _process = proc;
                _runningSecret = secretKey;
            }
            SetState(AgentRunState.Starting);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Consider it running once it stays up briefly (it keeps a persistent control connection).
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
            {
                bool up;
                lock (_lock)
                {
                    up = myGen == _generation && _process is { HasExited: false };
                    if (up) _reachedRunning = true;
                }
                if (up) SetState(AgentRunState.Running);
            });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ConsoleLogService.Shared.Log("Playit", $"Could not start the Playit agent: {ex.Message}");
            SetState(AgentRunState.Failed);
        }
    }

    /// <summary>Stops the agent process (called on app shutdown or disconnect).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _generation++; // invalidate the running process's Exited handler (this stop is intentional)
            TryKill();
            _process = null;
            _runningSecret = null;
        }
        SetState(AgentRunState.Stopped);
    }

    private void TryKill()
    {
        try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private void OnAgentLine(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        ConsoleLogService.Shared.Log("Playit", e.Data);
        lock (_lock)
        {
            _recentOutput.Add(e.Data);
            if (_recentOutput.Count > 12) _recentOutput.RemoveAt(0);
        }
    }

    // Strips playitd's log prefix ("2026-…Z ERROR playitd::daemon: ") to leave the human message.
    private static readonly Regex LogPrefix =
        new(@"^\S+Z\s+(TRACE|DEBUG|INFO|WARN|ERROR)\s+[\w:]+:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Turns playitd's raw output into a short, human-readable failure reason for the UI.</summary>
    private static string BuildFailureReason(List<string> rawLines, int? exitCode)
    {
        var cleaned = rawLines
            .Select(l => LogPrefix.Replace(l, "").Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Prefer the lines that actually explain the failure.
        var errs = cleaned.Where(l =>
            l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("failed", StringComparison.OrdinalIgnoreCase)).ToList();

        var pick = (errs.Count > 0 ? errs : cleaned).TakeLast(2).ToList();
        var msg = string.Join(" — ", pick);

        // If nothing informative was captured, at least report the exit code so it isn't a black box.
        if (msg.Length == 0)
            msg = exitCode is int c ? $"the agent exited (code {c})" : "the agent stopped unexpectedly";
        return msg;
    }

    /// <summary>Downloads the agent binary to <see cref="AgentDir"/> if not already there. Returns its path.</summary>
    private async Task<string> EnsureBinaryAsync()
    {
        var path = BinaryPath;
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        SetState(AgentRunState.Downloading);
        Directory.CreateDirectory(AgentDir);
        var url = $"https://github.com/playit-cloud/playit-agent/releases/download/{AgentVersion}/{AssetName}";

        var tmp = path + ".download";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* best-effort */ }
        }
        return path;
    }
}
