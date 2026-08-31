using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// The two characters a Paper or Purpur server refuses to run from.
/// </summary>
/// <remarks>
/// <para>
/// Paper will not start from a path containing <c>+</c> or <c>!</c>, and says so and exits. The two
/// are rejected at different stages and with different wording: <c>!</c> stops Paperclip before the
/// server exists at all ("Paperclip may not run in a directory containing '!'"), while <c>+</c> gets
/// as far as CraftBukkit's own check ("Cannot run server in a directory with ! or + in the
/// pathname"). Both leave a process that exits cleanly, which the launcher reads as a crash and
/// retries three times.
/// </para>
/// <para>
/// It applies to the Bukkit family only. The mod loaders and Vanilla run from those paths quite
/// happily, so a folder that has worked for months stops working the moment it becomes Paper — with
/// nothing linking the rename-your-folder message to the type change that caused it.
/// </para>
/// </remarks>
public static class BukkitPathRule
{
    /// <summary>The characters, in the order the error messages mention them.</summary>
    private static readonly char[] Rejected = { '!', '+' };

    /// <summary>Whether this server type cares about the path at all.</summary>
    public static bool Applies(ServerType type) => ServerTypeCatalog.IsPluginBased(type);

    /// <summary>
    /// The first offending character in <paramref name="path"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The whole absolute path, not just the server's own folder name: the check is on the working
    /// directory, so a <c>+</c> anywhere above it — in a user name, in a Dropbox folder — rejects
    /// every Paper server underneath just the same.
    /// </remarks>
    public static char? OffendingCharacter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }   // malformed path: judge what we were given

        var index = full.IndexOfAny(Rejected);
        return index >= 0 ? full[index] : null;
    }

    /// <summary>Whether this type would refuse to start from this path.</summary>
    public static bool Rejects(string? path, ServerType type) =>
        Applies(type) && OffendingCharacter(path) is not null;

    /// <summary>
    /// Whether the offending character is in the server's own folder rather than above it.
    /// </summary>
    /// <remarks>
    /// The difference decides whether the app may offer to fix it. Renaming the server's own folder
    /// affects only that server; renaming a parent would move everything else living under it too,
    /// which is not the app's to do.
    /// </remarks>
    public static bool IsInServerFolderName(string? path)
    {
        if (OffendingCharacter(path) is null) return false;

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!)));
        return name.IndexOfAny(Rejected) >= 0;
    }

    /// <summary>
    /// The same path with the offending characters replaced by a hyphen, or null when there is
    /// nothing to fix or the problem is in a parent folder.
    /// </summary>
    /// <remarks>
    /// A hyphen rather than nothing: "Java+Bedrock" becoming "JavaBedrock" reads like a typo, while
    /// "Java-Bedrock" reads like the name the user meant. Only the last segment is touched.
    /// </remarks>
    public static string? SuggestCleanPath(string? path)
    {
        if (!IsInServerFolderName(path)) return null;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!));
        var parent = Path.GetDirectoryName(full);
        if (parent is null) return null;

        var cleaned = Path.GetFileName(full);
        foreach (var c in Rejected) cleaned = cleaned.Replace(c, '-');

        return Path.Combine(parent, cleaned);
    }

    /// <summary>Whether a console line is the server refusing to run because of its path.</summary>
    /// <remarks>
    /// Both wordings, because they come from different programs. Matched so an existing server that
    /// hits this gets an explanation rather than three silent restarts — the checks above stop it
    /// being reached in the first place, but only for servers created or converted from now on.
    /// </remarks>
    public static bool IsPathRejection(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        var lower = line.ToLowerInvariant();

        return lower.Contains("cannot run server in a directory with")
            || lower.Contains("may not run in a directory containing");
    }
}
