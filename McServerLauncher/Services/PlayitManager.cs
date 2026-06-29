using System.ServiceProcess;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Playit.gg integration relying on the Windows SERVICE that playit installs
/// (usually "playitd"), which already keeps the tunnels running 24/7 in the background.
/// The app does not launch its own playit.exe: it only queries and, if possible, starts/stops the service.
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

    /// <summary>True if the playit service is installed on the system.</summary>
    public bool IsInstalled { get; private set; }

    public bool IsRunning => State == PlayitState.Running;

    /// <summary>Queries the current service status and updates <see cref="State"/>.</summary>
    public void RefreshState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
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

    /// <summary>Starts the playit service (may require administrator permissions).</summary>
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

    /// <summary>Stops the playit service (may require administrator permissions).</summary>
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
