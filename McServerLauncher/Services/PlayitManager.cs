using System.ServiceProcess;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Integración con Playit.gg apoyándose en el SERVICIO de Windows que instala playit
/// (normalmente "playitd"), que ya mantiene los túneles en segundo plano 24/7.
/// La app no lanza su propio playit.exe: solo consulta y, si se puede, arranca/detiene el servicio.
/// </summary>
public class PlayitManager
{
    private const string ServiceName = "playitd";

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

    /// <summary>True si el servicio de playit está instalado en el sistema.</summary>
    public bool IsInstalled { get; private set; }

    public bool IsRunning => State == PlayitState.Running;

    /// <summary>Consulta el estado actual del servicio y actualiza <see cref="State"/>.</summary>
    public void RefreshState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            var status = sc.Status; // lanza si el servicio no existe
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

    /// <summary>Arranca el servicio de playit (puede requerir permisos de administrador).</summary>
    public async Task StartServiceAsync()
    {
        await Task.Run(() =>
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
        });
        RefreshState();
    }

    /// <summary>Detiene el servicio de playit (puede requerir permisos de administrador).</summary>
    public async Task StopServiceAsync()
    {
        await Task.Run(() =>
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.CanStop && sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        });
        RefreshState();
    }
}
