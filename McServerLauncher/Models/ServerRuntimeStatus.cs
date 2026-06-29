namespace McServerLauncher.Models;

/// <summary>Run state of a server.</summary>
public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping
}

/// <summary>State of the Playit.gg tunnel.</summary>
public enum PlayitState
{
    Stopped,
    Starting,
    Running
}
