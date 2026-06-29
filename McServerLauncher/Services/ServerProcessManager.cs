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

    /// <summary>Raised for each output line (stdout or stderr) from the server.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when the server state changes.</summary>
    public event Action<ServerState>? StateChanged;

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
            if (IsRunning)
                return;

            if (!File.Exists(config.JarFullPath))
                throw new FileNotFoundException($"Server .jar not found: {config.JarFullPath}");

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
            OutputReceived?.Invoke(string.Format(Localizer.Get("Msg_LauncherStarting"), config.JavaPath, args));

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                // The server stays "Starting" until we detect "Done"; we promote it
                // to Running as soon as we see that line (see OnOutputData).
                State = ServerState.Running;
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
        return $"-Xms{config.MinRamGb}G -Xmx{config.MaxRamGb}G {extra}-jar \"{config.JarFile}\" nogui";
    }

    private void OnOutputData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        OutputReceived?.Invoke(e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        OutputReceived?.Invoke(Localizer.Get("Msg_ServerStopped"));
        lock (_lock)
        {
            _process?.Dispose();
            _process = null;
        }
        State = ServerState.Stopped;
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
            TryKill(proc);
        }
        catch
        {
            TryKill(proc);
        }
    }

    private void TryKill(Process proc)
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
    }
}
