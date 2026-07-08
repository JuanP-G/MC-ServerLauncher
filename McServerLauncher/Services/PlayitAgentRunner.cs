using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

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

    // Single source of truth for the pinned Playit agent version. It MUST stay in lockstep across
    // THREE places, all keyed off these numbers: (1) the version the app registers via create_agent
    // (PlayitPartnerService reuses these), (2) the GitHub release we download (AgentVersion tag), and
    // (3) the pinned per-asset hashes in AssetSha256 below. Bump them together — a version change
    // without matching hashes refuses to run, and one that doesn't match the registered variant fails
    // create_agent (AgentVariantVersionNotFound).
    public const int VersionMajor = 1;
    public const int VersionMinor = 0;
    public const int VersionPatch = 10;

    // GitHub release tag for the pinned version (derived, so the numbers above are the only source).
    private static readonly string AgentVersion = $"v{VersionMajor}.{VersionMinor}.{VersionPatch}";

    // Pinned SHA-256 of each v1.0.10 release asset we may download. The agent binary runs with the
    // user's key and is the highest-privilege code the app fetches, so — like every other download
    // (Mojang/Adoptium/Paper/Modrinth and our own installer) — it is checksum-verified before running.
    // Because AgentVersion is pinned, these are known constants; bump them whenever AgentVersion changes.
    private static readonly Dictionary<string, string> AssetSha256 = new(StringComparer.Ordinal)
    {
        ["playit-windows-x86_64-signed.exe"] = "2dbdaad119844cbbc062cc9774b8b462afa5f1b4b7832a9fc5ef4676cae887cf",
        ["playit-windows-x86-signed.exe"]    = "9cec088fa4ee9ad4d59acd27512cf914078bdb31742e9abc946b81ea705f9d35",
        ["playit-linux-amd64"]               = "2df7d9f10227ab312b1ad341853db4e8a8243df5cfcdbae58713a4271711c339",
        ["playit-linux-aarch64"]             = "4c0db3e7b3a8158e249441c2f0b73f54e83429395890c7b1ca45fd7a6303d763",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly object _lock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1); // serialize StartAsync so two calls can't both launch
    private Process? _process;
    private string? _runningSecret;
    private string? _lastSecret; // last secret we were asked to run (for HasSecret / RetryAsync)
    private bool _reachedRunning;
    private int _generation; // bumped on each start/stop so a replaced or intentionally-killed process is ignored
    private readonly List<string> _recentOutput = new();

    public AgentRunState State { get; private set; } = AgentRunState.Stopped;

    /// <summary>Human-readable reason for the last <see cref="AgentRunState.Failed"/> (for the UI).</summary>
    public string? LastError { get; private set; }

    /// <summary>True once the app has an agent key to run (i.e. the user connected their Playit account).</summary>
    public bool HasSecret => !string.IsNullOrWhiteSpace(_lastSecret);

    /// <summary>(Re)starts the agent with the last known secret. No-op if we never had one.</summary>
    public Task RetryAsync() => _lastSecret is { } s ? StartAsync(s) : Task.CompletedTask;

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
        _lastSecret = secretKey;
        if (AssetName is null) { SetState(AgentRunState.Unsupported); return; }

        // Serialize starts: without this, two near-simultaneous calls (e.g. app-startup + connect)
        // could both pass the "already running" check during the download window and launch two
        // agents, which then collide on the pipe.
        await _startGate.WaitAsync();
        try
        {
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

                // A playitd from a previous session (ungraceful exit, app killed, closed to tray then
                // terminated…) may still be listening on our private pipe; playitd would then refuse
                // to start with "another instance is already running". It runs from our own path, so
                // it's safe to reclaim — this never touches a system-wide Playit the user installed.
                KillStrayAgents();

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
        finally
        {
            _startGate.Release();
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
            _lastSecret = null; // disconnected: no key to retry with
        }
        SetState(AgentRunState.Stopped);
    }

    private void TryKill()
    {
        try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// <summary>
    /// Kills any leftover agent process running from <em>our</em> binary path (an orphan from a prior
    /// session still holding the private pipe). Matching on the full image path means we never touch a
    /// system-wide Playit the user installed separately (different path, and usually not even killable).
    /// </summary>
    private static void KillStrayAgents()
    {
        try
        {
            var ourPath = BinaryPath;
            foreach (var p in Process.GetProcessesByName("playitd"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, ourPath, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000); // let the OS release the named pipe before we rebind it
                    }
                }
                catch { /* not ours (access denied) or already gone */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* best-effort cleanup */ }
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

    /// <summary>
    /// Downloads the agent binary to <see cref="AgentDir"/> if not already there, and returns its path.
    /// The binary runs as a child process with the user's agent key, so it's the highest-privilege code
    /// the app fetches: it is <b>verified against a pinned SHA-256</b> (of the exact <see cref="AgentVersion"/>
    /// asset) before it is ever executed — on mismatch it is deleted and this throws, so a tampered or
    /// corrupted binary is never trusted. A cached copy is re-checked too (a poisoned cache is re-fetched).
    /// </summary>
    private async Task<string> EnsureBinaryAsync()
    {
        var path = BinaryPath;
        var expectedHash = AssetName is { } asset && AssetSha256.TryGetValue(asset, out var h) ? h : null;
        if (string.IsNullOrEmpty(expectedHash))
            // No pinned hash => refuse to run an unverifiable native binary (should never happen: every
            // supported AssetName has a pinned hash).
            throw new InvalidOperationException("No pinned checksum for the Playit agent binary on this platform.");

        // Reuse a cached binary only if it still matches the pinned hash.
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            var cachedHash = await DownloadVerifier.ComputeHashAsync(path, HashAlgorithmName.SHA256);
            if (string.Equals(cachedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return path;
            TryDeleteFile(path); // stale or tampered — re-download and re-verify
        }

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

        // Verify BEFORE it is ever moved into place / executed: deletes tmp and throws on mismatch.
        await DownloadVerifier.VerifyAsync(tmp, expectedHash, HashAlgorithmName.SHA256);

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

    private static void TryDeleteFile(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); }
        catch { /* best-effort */ }
    }
}
