using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Recognises the server saying it has finished writing the world to disk.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a backup of a running server trustworthy. After <c>save-all flush</c> the
/// server writes everything it was holding and then says so; zipping before that line would copy a
/// world the JVM had not finished with.
/// </para>
/// <para>
/// The whole message has to be the confirmation, starting right where the log's own prefix ends
/// (<see cref="ConsoleLineClassifier.MessageBody"/>): a player can type "Saved the game" in chat,
/// and that line also passes through the console. Matching it would let anyone on the server cut a
/// backup short. Looking for <c>]: Saved the game</c> anywhere in the line was not enough — a
/// player who typed <c>x]: Saved the game</c> produced exactly that.
/// </para>
/// </remarks>
public static partial class SaveConfirmation
{
    /// <summary>Whether this console line is the server confirming a save.</summary>
    public static bool IsSaveFinished(string line) =>
        ConsoleLineClassifier.MessageBody(line) is { } body && SavedRegex().IsMatch(body);

    // "Saved the game" from 1.13 on; "Saved the world" before it.
    [GeneratedRegex(@"^Saved the (game|world)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SavedRegex();
}
