using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Reads Pumpkin's console, which is not written the way a Java server writes its own.
/// </summary>
/// <remarks>
/// <para>
/// Every detector in the app — the player list, the idle timer that stops an empty server, chat,
/// the colours — starts from <see cref="ConsoleLineClassifier.MessageBody"/>, which looks for the
/// <c>]: </c> that ends a Java log prefix. Pumpkin has no such prefix. Captured from Pumpkin
/// 0.2.2 run through a pipe, after <see cref="AnsiEscapes.Strip"/>:
/// </para>
/// <code>
/// 14:14:38  INFO Started server; took 168ms
/// 14:16:23  INFO Alex joined the game
/// 14:16:35  WARN Closing connection for 1: You were kicked from the game
/// 14:16:37  INFO Alex left the game
/// There are 1 of a max of 1000 players online: Alex
/// </code>
/// <para>
/// Left as it is, nobody would ever be seen joining, and a Pumpkin server with people on it would
/// be stopped for being empty. So each line is rewritten once, where it arrives, into the shape the
/// rest of the app already reads — <c>[14:16:23 INFO]: Alex joined the game</c> — instead of
/// teaching every detector a second format. What the server said is unchanged; only the brackets
/// around its timestamp and level are the app's.
/// </para>
/// <para>
/// Chat needs one more step. Pumpkin logs it as <c>&lt;chat&gt; Bob: hello</c>, and read as a Java
/// line that is a player called "chat". It becomes <c>&lt;Bob&gt; hello</c>, which is also what
/// keeps "Bob: Alice joined the game" typed in chat from ever being read as Alice joining.
/// </para>
/// <para>
/// <strong>Only the default format is recognised.</strong> <c>[logging]</c> in <c>pumpkin.toml</c>
/// can add thread names and module paths or change the timestamp, and with those a player's name is
/// no longer the first thing in the message — there is no telling "Alex joined the game" from a
/// thread called Alex. A line in any other shape is passed through untouched and reported as not
/// recognised, and <see cref="ServerProcessManager.PlayerTrackingReliable"/> turns the idle
/// shutdown off for that run rather than guess.
/// </para>
/// </remarks>
public static partial class PumpkinConsole
{
    /// <summary>The sentence Pumpkin logs once it accepts players. There is no "Done (" on Pumpkin.</summary>
    internal const string ReadyMarker = "Started server; took ";

    /// <summary>One line of Pumpkin's console, as the rest of the app should read it.</summary>
    /// <param name="Text">The line in the Java shape when it was recognised; the original otherwise.</param>
    /// <param name="Recognised">
    /// True when the line began with a time and a level, as Pumpkin's default prefix does. A line
    /// without any prefix — the answer to a console command, which Pumpkin prints bare — is not
    /// recognised, and that is fine. It is not proof of the default format either (a module path
    /// after the level looks the same): only <see cref="IsReady"/> is, on the one line whose text is
    /// known in advance.
    /// </param>
    public readonly record struct Line(string Text, bool Recognised);

    /// <summary>Rewrites a line in Pumpkin's default format into the Java shape.</summary>
    /// <param name="line">The line after <see cref="AnsiEscapes.Strip"/>.</param>
    public static Line Normalize(string line)
    {
        var match = DefaultPrefix().Match(line);
        if (!match.Success) return new Line(Unprefixed(line), false);

        var message = match.Groups["message"].Value;

        // "<chat> Bob: hello" -> "<Bob> hello". Anchored at the start of the message and only for a
        // real name, so a line that merely mentions "<chat>" stays what it was.
        var chat = Chat().Match(message);
        if (chat.Success && PlayerNames.IsStorable(chat.Groups["name"].Value))
            message = $"<{chat.Groups["name"].Value}> {chat.Groups["text"].Value}";

        return new Line($"[{match.Groups["time"].Value} {match.Groups["level"].Value}]: {message}", true);
    }

    /// <summary>
    /// A line with no prefix must never be taken for a player joining or leaving.
    /// </summary>
    /// <remarks>
    /// Pumpkin logs both with its prefix, always. But <see cref="ConsoleLineClassifier.NameBefore"/>
    /// also reads a bare line, and a bare "Alex left the game" — the second line of something
    /// multi-line, should a client ever get a line break into a message — would take Alex off the
    /// list and let the idle timer stop the server under them. Indented, it reads as the
    /// continuation it is, and what stands before the marker is no longer a name.
    /// <para>
    /// The one bare line that is given a prefix is the answer to <c>save-all</c>. A copy of the world
    /// taken while the server runs waits for exactly that sentence
    /// (<see cref="SaveConfirmation"/>), which reads it from behind a log prefix like everything
    /// else; bare, every such copy waited out its whole timeout first.
    /// </para>
    /// </remarks>
    private static string Unprefixed(string line)
    {
        if (line.Trim() == SavedAnswer) return CommandAnswerPrefix + SavedAnswer;

        return line.Contains(" joined the game", StringComparison.Ordinal)
               || line.Contains(" left the game", StringComparison.Ordinal)
            ? "  " + line
            : line;
    }

    /// <summary>What Pumpkin answers once <c>save-all</c> has finished, with no prefix of its own.</summary>
    private const string SavedAnswer = "Saved the game";

    /// <summary>The prefix the app puts on that answer. No time: Pumpkin gave none.</summary>
    private const string CommandAnswerPrefix = "[Pumpkin INFO]: ";

    /// <summary>Whether a normalised line is Pumpkin saying it finished starting.</summary>
    /// <remarks>
    /// From the start of the message, like every other detector: once <see cref="Normalize"/> has
    /// run, anything a player types begins with <c>&lt;name&gt;</c>.
    /// </remarks>
    public static bool IsReady(Line line) =>
        line.Recognised
        && ConsoleLineClassifier.MessageBody(line.Text) is { } body
        && body.StartsWith(ReadyMarker, StringComparison.Ordinal);

    /// <summary>
    /// Whether a line Pumpkin wrote in some other format still says it finished starting.
    /// </summary>
    /// <remarks>
    /// Only so that a server whose owner changed <c>[logging]</c> does not sit on "Starting" for
    /// ever. It is deliberately loose, and so nothing else is concluded from such a run: see
    /// <see cref="ServerProcessManager.PlayerTrackingReliable"/>.
    /// </remarks>
    public static bool LooksReady(string line) => line.Contains(ReadyMarker, StringComparison.Ordinal);

    // "14:16:23  INFO message": the time, the level right-aligned to five characters, one space.
    [GeneratedRegex(@"^(?<time>\d{2}:\d{2}:\d{2}) +(?<level>TRACE|DEBUG|INFO|WARN|ERROR) (?<message>.*)$")]
    private static partial Regex DefaultPrefix();

    // What may be a name is PlayerNames' to say, not this pattern's: it only finds where one would be.
    [GeneratedRegex(@"^<chat> (?<name>[^\s:]+): (?<text>.*)$")]
    private static partial Regex Chat();
}
