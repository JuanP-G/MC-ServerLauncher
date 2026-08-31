namespace McServerLauncher.Models;

/// <summary>Where a console line came from, before anything tries to read it.</summary>
/// <remarks>
/// The distinction the process used to throw away. <c>ServerProcessManager</c> wired standard output
/// and standard error to the same handler and re-emitted both through one event that said nothing
/// about which was which — and standard error is the cheapest and most reliable severity signal a
/// server ever gives you. <see cref="Launcher"/> is the third because that same service also injects
/// its own messages through that event ("starting", "stopped", "not responding"), and those are the
/// app talking, not the server.
/// </remarks>
public enum ConsoleSource
{
    /// <summary>The server's standard output.</summary>
    Stdout,

    /// <summary>The server's standard error.</summary>
    Stderr,

    /// <summary>This application, not the server.</summary>
    Launcher
}

/// <summary>What a console line is about, which is what decides its colour and its filter.</summary>
/// <remarks>
/// Seven rather than the usual five. <see cref="Launcher"/> and <see cref="Players"/> are the two
/// worth adding: the app's own messages and the server's were indistinguishable in one grey stream,
/// and "who came and went while I was away" is the question people actually scroll back for.
/// </remarks>
public enum ConsoleLineKind
{
    /// <summary>Ordinary server output. The vast majority of lines.</summary>
    Info,

    /// <summary>The server warned about something.</summary>
    Warn,

    /// <summary>The server failed at something, or wrote to standard error.</summary>
    Error,

    /// <summary>A player said something to the other players.</summary>
    Chat,

    /// <summary>Somebody joined, left or died.</summary>
    Players,

    /// <summary>A command typed in the box, echoed back.</summary>
    Command,

    /// <summary>This application talking, not the server.</summary>
    Launcher
}

/// <summary>One line in a server's console.</summary>
/// <param name="Text">The line exactly as it was written. Never reformatted.</param>
/// <param name="Kind">What it is about.</param>
public sealed record ConsoleLine(string Text, ConsoleLineKind Kind)
{
    /// <summary>
    /// The line's text, so anything that stringifies a line keeps getting a line.
    /// </summary>
    /// <remarks>
    /// Not decoration, and not optional. The console's copy — both the context menu and Ctrl+C —
    /// goes through <c>o?.ToString()</c> over the selected items. A positional record generates a
    /// <c>ToString</c> that prints <c>ConsoleLine { Text = …, Kind = … }</c>, so without this the
    /// clipboard would quietly start filling with that instead of the log, compiling cleanly and
    /// failing only where nobody would look for it: in whatever the user pasted it into.
    /// </remarks>
    public override string ToString() => Text;
}
