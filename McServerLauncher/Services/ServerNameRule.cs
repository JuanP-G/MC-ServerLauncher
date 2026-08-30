using System.IO;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What is wrong with a server's folder name, when something is.</summary>
public enum NameIssueKind
{
    /// <summary>A character Windows does not allow in a file name at all.</summary>
    InvalidCharacter,

    /// <summary>A name Windows reserves for a device — CON, NUL, COM1 and friends.</summary>
    ReservedName,

    /// <summary>Ends in a dot or a space, which Windows quietly trims.</summary>
    TrailingDotOrSpace,

    /// <summary>A character the server software itself refuses to run from.</summary>
    ServerRejectsCharacter,

    /// <summary>That character is in a folder above, so it is not ours to rename.</summary>
    ServerRejectsParentCharacter
}

/// <summary>The problem found, and whatever detail the message needs.</summary>
public record NameIssue(NameIssueKind Kind, string Detail);

/// <summary>
/// Checking a server's folder name before it becomes a server that will not start.
/// </summary>
/// <remarks>
/// <para>
/// The app used to strip whatever Windows forbids and say nothing: type <c>Mi:Server</c> and a
/// folder called <c>MiServer</c> appeared, with no hint that the name had been altered. Nothing else
/// was checked at all, and the parent folder was never looked at — which is how a server sat in
/// <c>Java+Bedrock</c> for weeks and then refused to start the day it became Paper.
/// </para>
/// <para>
/// The list of characters a server refuses is deliberately short, and it was measured rather than
/// guessed: a real Paper server was started in a folder named after each of twenty-one candidates.
/// Only <c>!</c> and <c>+</c> failed. <c>#</c>, <c>&amp;</c>, <c>%</c>, <c>^</c>, <c>~</c>, <c>'</c>,
/// <c>;</c>, <c>,</c>, <c>=</c>, <c>@</c>, <c>$</c>, brackets, braces, parentheses, spaces, accents,
/// underscores and dots all ran fine. Banning those on suspicion would have refused names like
/// <c>Server (2026)</c> or <c>Iberia #2</c> for no reason at all.
/// </para>
/// </remarks>
public static class ServerNameRule
{
    /// <summary>Device names Windows reserves, with or without an extension.</summary>
    /// <remarks>
    /// Creating a folder called <c>CON</c> fails outright on Windows. It is not a character to strip
    /// and it is not something a user would suspect, which is exactly why it is worth saying.
    /// </remarks>
    private static readonly string[] Reserved =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// The first problem with the folder a server would live in, or null when there is none.
    /// </summary>
    /// <param name="folderPath">The full path the server folder would have.</param>
    /// <param name="type">The server type, which decides whether the software adds rules of its own.</param>
    /// <remarks>
    /// Ordered by how fundamental each problem is: a name Windows cannot create at all is reported
    /// before one that merely stops Paper, because fixing the second would leave the first.
    /// </remarks>
    public static NameIssue? Check(string? folderPath, ServerType type)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;

        // Not trimmed: a trailing space is one of the things being checked for, and trimming it
        // here would quietly hide it. The dialog trims what the user types, which is right there
        // and separate from judging the path it was given.
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
        if (string.IsNullOrEmpty(name)) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var offending = name.FirstOrDefault(c => invalid.Contains(c));
        if (offending != '\0')
            return new NameIssue(NameIssueKind.InvalidCharacter, offending.ToString());

        // The next two are Windows rules and are checked only there. On Linux and macOS a folder
        // called CON, or one ending in a dot, is perfectly ordinary — refusing it would be the app
        // inventing a problem, which is the thing this class exists not to do.
        if (OperatingSystem.IsWindows())
        {
            // The extension is included in the comparison: "CON.txt" is reserved too.
            var stem = Path.GetFileNameWithoutExtension(name);
            if (Reserved.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase)))
                return new NameIssue(NameIssueKind.ReservedName, name);

            // Windows trims these on creation, so the folder ends up with a different name from the
            // one saved in servers.json, and the server is "missing" next time the app looks.
            if (name.EndsWith('.') || name.EndsWith(' '))
                return new NameIssue(NameIssueKind.TrailingDotOrSpace, name);
        }

        if (BukkitPathRule.Rejects(folderPath, type))
        {
            var bad = BukkitPathRule.OffendingCharacter(folderPath)!.Value;
            return new NameIssue(
                BukkitPathRule.IsInServerFolderName(folderPath)
                    ? NameIssueKind.ServerRejectsCharacter
                    : NameIssueKind.ServerRejectsParentCharacter,
                bad.ToString());
        }

        return null;
    }

    /// <summary>
    /// The name with the characters Windows forbids taken out, and trailing dots and spaces trimmed.
    /// </summary>
    /// <remarks>
    /// Offered as a suggestion now rather than applied behind the user's back. The old behaviour
    /// silently produced a folder with a different name from the one they typed.
    /// </remarks>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Where(c => !invalid.Contains(c)).ToArray());

        return cleaned.TrimEnd('.', ' ');
    }
}
