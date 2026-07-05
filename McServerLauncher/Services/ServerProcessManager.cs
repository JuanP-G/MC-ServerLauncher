using System.Diagnostics;
using System.IO;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Manages the lifecycle of a Minecraft server's java process:
/// startup, clean shutdown (sending "stop" over stdin), command sending and
/// re-emitting the console output in real time.
/// </summary>
public class ServerProcessManager
{
    private Process? _process;
    private readonly object _lock = new();
    private DateTime _startedAtUtc;

    /// <summary>Raised for each output line (stdout or stderr) from the server.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when the server state changes.</summary>
    public event Action<ServerState>? StateChanged;

    /// <summary>
    /// Raised when the process ends WITHOUT us having asked it to (i.e. it crashed, was killed
    /// externally, or the JVM exited on its own) — not raised for a clean Stop/StopAsync, including
    /// the forced-kill-after-timeout path (that's still a stop WE requested). The exit code is
    /// passed when available.
    /// </summary>
    public event Action<int?>? UnexpectedExit;

    /// <summary>When the current (or most recently started) process was launched.</summary>
    public DateTime StartedAtUtc => _startedAtUtc;

    private ServerState _state = ServerState.Stopped;
    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public bool IsRunning => State is ServerState.Running or ServerState.Starting or ServerState.Stopping;

    /// <summary>The running process (for CPU/RAM stats). Null if there is no active server.</summary>
    public Process? CurrentProcess
    {
        get { lock (_lock) { return _process; } }
    }

    /// <summary>Starts the server. Equivalent to run.bat but without a console window.</summary>
    public void Start(ServerConfig config)
    {
        lock (_lock)
        {
            // Stopping (including the kill-after-timeout path in StopAsync) waits for the process
            // to actually die before returning, so this should be rare; still, fail loudly instead
            // of silently no-op-ing if something calls Start() while a stop is still in flight.
            if (IsRunning)
                throw new InvalidOperationException(Localizer.Get("Msg_ServerStillStopping"));

            var isForgeArgs = !string.IsNullOrWhiteSpace(config.ForgeArgs);
            if (isForgeArgs)
            {
                if (ResolveForgeArgsFile(config) is null)
                    throw new FileNotFoundException(
                        $"Forge launch args not found under {Path.Combine(config.FolderPath, "libraries")}");
            }
            else if (!File.Exists(config.JarFullPath))
            {
                throw new FileNotFoundException($"Server .jar not found: {config.JarFullPath}");
            }

            var args = BuildJavaArguments(config);

            var psi = new ProcessStartInfo
            {
                FileName = config.JavaPath,
                Arguments = args,
                WorkingDirectory = config.FolderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputData;
            _process.ErrorDataReceived += OnOutputData;
            _process.Exited += OnProcessExited;

            State = ServerState.Starting;
            _startedAtUtc = DateTime.UtcNow;
            OutputReceived?.Invoke(string.Format(Localizer.Get("Msg_LauncherStarting"), config.JavaPath, args));

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                // The server stays "Starting" until it reports it finished loading;
                // OnOutputData promotes it to Running when it sees the "Done (...)!" line.
            }
            catch
            {
                State = ServerState.Stopped;
                _process?.Dispose();
                _process = null;
                throw;
            }
        }
    }

    private static string BuildJavaArguments(ServerConfig config)
    {
        var extra = string.IsNullOrWhiteSpace(config.ExtraJvmArgs) ? "" : config.ExtraJvmArgs.Trim() + " ";
        var mem = $"-Xms{config.MinRamGb}G -Xmx{config.MaxRamGb}G ";

        // Modern Forge (1.17+) has no runnable server jar: it is launched via an args file
        // generated by the installer (win_args.txt on Windows, unix_args.txt elsewhere).
        if (!string.IsNullOrWhiteSpace(config.ForgeArgs) && ResolveForgeArgsFile(config) is { } argsFile)
            return $"{mem}{extra}@{argsFile} nogui";

        return $"{mem}{extra}-jar \"{config.JarFile}\" nogui";
    }

    /// <summary>
    /// Locates the Forge launch args file relative to the server folder (so it can be passed as
    /// <c>@&lt;relative-path&gt;</c> with the working directory set to the folder). Tries the exact
    /// version dir first, then any forge dir. Returns null if not found.
    /// </summary>
    private static string? ResolveForgeArgsFile(ServerConfig config)
    {
        var argName = OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";
        var forgeRoot = Path.Combine(config.FolderPath, "libraries", "net", "minecraftforge", "forge");
        if (!Directory.Exists(forgeRoot))
            return null;

        var exact = Path.Combine(forgeRoot, config.ForgeArgs, argName);
        if (File.Exists(exact))
            return Path.GetRelativePath(config.FolderPath, exact);

        foreach (var dir in Directory.GetDirectories(forgeRoot))
        {
            var candidate = Path.Combine(dir, argName);
            if (File.Exists(candidate))
                return Path.GetRelativePath(config.FolderPath, candidate);
        }
        return null;
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        // Vanilla/Fabric/Forge/Paper all log a line like
        // `[12:00:00] [Server thread/INFO]: Done (3.2s)! For help, type "help"` when ready.
        if (State == ServerState.Starting && e.Data.Contains("Done (", StringComparison.Ordinal))
            State = ServerState.Running;
        OutputReceived?.Invoke(e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Capture BEFORE resetting to Stopped: State == Stopping means WE asked it to stop (either
        // the graceful "stop" command worked, or we killed it ourselves after a timeout); anything
        // else (Running, or Starting if it dies mid-boot) means it exited on its own — a crash.
        var wasRequested = State == ServerState.Stopping;
        int? exitCode = null;
        try { exitCode = (sender as Process)?.ExitCode; }
        catch { /* not always available; best-effort */ }

        OutputReceived?.Invoke(Localizer.Get("Msg_ServerStopped"));
        lock (_lock)
        {
            _process?.Dispose();
            _process = null;
        }
        State = ServerState.Stopped;

        if (!wasRequested)
            UnexpectedExit?.Invoke(exitCode);
    }

    /// <summary>Sends an arbitrary command over stdin (like typing it into the console).</summary>
    public void SendCommand(string command)
    {
        lock (_lock)
        {
            if (_process is null || _process.HasExited)
                return;
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
    }

    /// <summary>
    /// Stops the server cleanly: sends "stop" and waits for it to exit.
    /// If it doesn't exit within the given time, it kills the process tree.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Process? proc;
        lock (_lock)
        {
            proc = _process;
            if (proc is null || proc.HasExited)
                return;
            State = ServerState.Stopping;
        }

        try
        {
            OutputReceived?.Invoke(Localizer.Get("Msg_StoppingSaving"));
            proc.StandardInput.WriteLine("stop");
            proc.StandardInput.Flush();

            using var cts = new CancellationTokenSource(timeout);
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            OutputReceived?.Invoke(Localizer.Get("Msg_NotRespondingKill"));
            await KillAndWaitAsync(proc);
        }
        catch
        {
            await KillAndWaitAsync(proc);
        }

        // At this point the OS process is confirmed gone (either the graceful stop finished above,
        // or KillAndWaitAsync waited for the forced kill to land). Process.Exited - which is what
        // actually flips State away from Stopping via OnProcessExited - fires around the same time
        // but isn't guaranteed to have already run. A short bounded poll removes that race for
        // callers (Restart, in particular) that check IsRunning/call Start() right after this
        // method returns; in the vast majority of cases the event has already fired and this loop
        // exits on its first check.
        var settleDeadline = DateTime.UtcNow.AddSeconds(2);
        while (State == ServerState.Stopping && DateTime.UtcNow < settleDeadline)
            await Task.Delay(10);
    }

    /// <summary>
    /// Kills the process tree and waits (briefly) for the OS to actually finish tearing it down,
    /// instead of returning right after issuing the kill.
    /// </summary>
    private async Task KillAndWaitAsync(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have already exited.
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Extremely unlikely (the OS refused to tear down the tree in 5s), but don't hang
            // StopAsync forever over it; OnProcessExited will still fire whenever it does finish.
            OutputReceived?.Invoke(Localizer.Get("Msg_KillTimedOut"));
        }
        catch
        {
            // Process handle already gone, etc. - nothing more to wait on.
        }
    }
}
