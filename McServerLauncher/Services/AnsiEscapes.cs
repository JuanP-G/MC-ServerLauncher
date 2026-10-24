using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Removes the colour codes some servers write into their console output.
/// </summary>
/// <remarks>
/// <para>
/// Paper colours its console with ANSI escape sequences even when its output is a pipe, and since the
/// join and leave messages became coloured the line the app receives is
/// <c>[16:41:52 INFO]: ESC[38;5;11mBot1 joined the gameESC[0m</c>. Every detector reads the message
/// from the start (so that a player typing the same words in chat cannot fake it), and a name that
/// begins with an escape sequence is not a name: players joined Paper servers and the app never saw
/// them, which also left every statistic that counts players at zero.
/// </para>
/// <para>
/// The sequences are removed where the output enters the app, once, so nothing downstream (the
/// console, the log on disk, the player history, the speed probe) ever sees them.
/// </para>
/// </remarks>
public static partial class AnsiEscapes
{
    /// <summary>The text without colour or formatting sequences.</summary>
    public static string Strip(string text) =>
        text.Contains('\x1b') ? Sequence().Replace(text, "") : text;

    // CSI sequences: ESC [ parameters final-letter (colours, bold, reset, cursor moves).
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Sequence();
}
