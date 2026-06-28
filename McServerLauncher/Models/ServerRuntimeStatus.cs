namespace McServerLauncher.Models;

/// <summary>Estado de ejecución de un servidor.</summary>
public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping
}

/// <summary>Estado del túnel de Playit.gg.</summary>
public enum PlayitState
{
    Stopped,
    Starting,
    Running
}
