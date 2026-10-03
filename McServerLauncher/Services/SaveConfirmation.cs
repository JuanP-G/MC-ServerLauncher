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
/// Anchored after the log's own prefix, like <see cref="WorldSeed.FromConsoleLine"/>: a player can
/// type "Saved the game" in chat, and that line also passes through the console. Matching it would
/// let anyone on the server cut a backup short.
/// </para>
/// </remarks>
public static partial class SaveConfirmation
{
    /// <summary>Whether this console line is the server confirming a save.</summary>
    public static bool IsSaveFinished(string line) => SavedRegex().IsMatch(line);

    // "Saved the game" from 1.13 on; "Saved the world" before it.
    [GeneratedRegex(@"\]:\s*Saved the (game|world)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SavedRegex();
}
