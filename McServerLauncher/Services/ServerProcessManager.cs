using System.Diagnostics;
using System.IO;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Gestiona el ciclo de vida del proceso java de un servidor de Minecraft:
/// arranque, parada limpia (enviando "stop" por stdin), envío de comandos y
/// reemisión de la salida de consola en tiempo real.
/// </summary>
public class ServerProcessManager
{
    private Process? _process;
    private readonly object _lock = new();

    /// <summary>Se dispara por cada línea de salida (stdout o stderr) del servidor.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Se dispara cuando cambia el estado del servidor.</summary>
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

    /// <summary>Proceso en curso (para estadísticas de CPU/RAM). Null si no hay servidor activo.</summary>
    public Process? CurrentProcess
    {
        get { lock (_lock) { return _process; } }
    }

    /// <summary>Arranca el servidor. Equivale a run.bat pero sin ventana de consola.</summary>
    public void Start(ServerConfig config)
    {
        lock (_lock)
        {
            if (IsRunning)
                return;

            if (!File.Exists(config.JarFullPath))
                throw new FileNotFoundException($"No se encontró el .jar del servidor: {config.JarFullPath}");

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
                // El servidor está "Starting" hasta que detectemos el "Done"; lo elevamos
                // a Running en cuanto vemos esa línea (ver OnOutputData).
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

    /// <summary>Envía un comando arbitrario por stdin (como teclearlo en la consola).</summary>
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
    /// Detiene el servidor de forma limpia: envía "stop" y espera el cierre.
    /// Si no cierra en el tiempo indicado, mata el árbol de procesos.
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
            // El proceso ya pudo haber terminado.
        }
    }
}
