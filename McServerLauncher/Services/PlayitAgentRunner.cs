using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

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

    public AgentRunState State { get; private set; } = AgentRunState.Stopped;

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

        try
        {
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

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnAgentLine;
            proc.ErrorDataReceived += OnAgentLine;
            proc.Exited += (_, _) =>
            {
                lock (_lock) { _process = null; _runningSecret = null; }
                SetState(AgentRunState.Stopped);
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
                lock (_lock)
                {
                    if (_process is { HasExited: false }) SetState(AgentRunState.Running);
                }
            });
        }
        catch (Exception ex)
        {
            ConsoleLogService.Shared.Log("Playit", $"Could not start the Playit agent: {ex.Message}");
            SetState(AgentRunState.Failed);
        }
    }

    /// <summary>Stops the agent process (called on app shutdown or disconnect).</summary>
    public void Stop()
    {
        lock (_lock)
        {
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

    private static void OnAgentLine(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
            ConsoleLogService.Shared.Log("Playit", e.Data);
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
