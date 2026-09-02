namespace McServerLauncher.ViewModels;

/// <summary>
/// The parts of the app the left rail switches between.
/// </summary>
/// <remarks>
/// <para>
/// The window used to put three different scopes in one column: Playit, which belongs to the
/// account; the mods, which belong to the content; and start/stop, which belongs to the server.
/// All three sat at the same visual height, and the Playit block took three rows of it whether or
/// not there was a tunnel — which is what pushed the console, the thing you actually read when
/// something breaks, to halfway down the window.
/// </para>
/// <para>
/// Only what the rail <i>navigates</i> is here. Settings and About are dialogs, not sections: they
/// already existed as dialogs, they are visited and dismissed rather than worked in, and giving
/// them a pane each would have been change for its own sake. The rail shows them alongside these
/// because to the person clicking, "take me to the settings" and "take me to my tunnels" are the
/// same kind of request — but only these two swap what fills the window.
/// </para>
/// </remarks>
public enum AppSection
{
    /// <summary>The server list and the detail of whichever is selected.</summary>
    Servers,

    /// <summary>Playit: the connection, the agent, and the tunnels.</summary>
    Tunnels,
}
