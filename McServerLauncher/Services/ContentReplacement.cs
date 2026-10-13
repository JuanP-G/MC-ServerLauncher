using System.Collections.Generic;
using System.IO;
using System.Linq;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Services;

/// <summary>
/// What a newly installed jar replaces, and taking those out without ever leaving none.
/// </summary>
/// <remarks>
/// <para>
/// Installing a new version of a mod used to remove one file: the row it was started from, or the
/// copies Modrinth recognised. Everything else stayed — an older duplicate under another name, a
/// disabled copy, any jar the store did not know, and every jar at all when the store did not
/// answer. Each of those is either junk on the disk or, if enabled, a second copy of the mod that
/// the loader refuses to start with.
/// </para>
/// <para>
/// So a jar is another copy of the new one when any of three things says so: the store puts it in
/// the same project; the jar gives itself the same id; or it is the new file's own name with
/// <c>.disabled</c> on the end. The jar's id is what makes this work offline and for jars the store
/// never heard of — it is read from the file, and no request can fail to answer it.
/// </para>
/// </remarks>
internal static class ContentReplacement
{
    /// <summary>The extension a jar wears between being taken out and being deleted.</summary>
    /// <remarks>
    /// Not <c>.jar</c>, so no loader picks it up, and not <c>.jar.disabled</c>, so the content tab
    /// does not list it. Anything left wearing it is swept by <see cref="SweepLeftovers"/>.
    /// </remarks>
    internal const string RemovingSuffix = ".mcsl-removing";

    /// <summary>How taking the old copies out went.</summary>
    internal enum RemoveResult
    {
        /// <summary>Every old copy is gone.</summary>
        Done,

        /// <summary>
        /// One of them is in use — the server is running. Nothing was removed, and the new jar was
        /// taken back out if that could be done safely.
        /// </summary>
        Locked
    }

    /// <summary>
    /// The jars in <paramref name="folder"/> that are another copy of <paramref name="newJarPath"/>.
    /// </summary>
    /// <param name="folder">Every jar in the content folder, from <see cref="ContentManifest.ReadAll"/>.</param>
    /// <param name="newJarPath">The jar just installed. Never its own victim.</param>
    /// <param name="newOwnId">The id the new jar gives itself, or null if it gives none.</param>
    /// <param name="projectId">The new jar's store project, or null if unknown.</param>
    /// <param name="projectByPath">Which store project each existing jar is; may be empty.</param>
    internal static IReadOnlyList<string> Select(
        IReadOnlyList<ContentManifest.Entry> folder, string newJarPath, string? newOwnId,
        string? projectId, IReadOnlyDictionary<string, string>? projectByPath)
    {
        var newFull = Path.GetFullPath(newJarPath);
        var sameProject = projectId is not null && projectByPath is not null
            ? ServerModsViewModel.ReplacedBy(projectByPath, projectId, newJarPath)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var victims = new List<string>();
        foreach (var entry in folder)
        {
            var full = Path.GetFullPath(entry.Path);
            if (string.Equals(full, newFull, StringComparison.OrdinalIgnoreCase)) continue;

            var staleDisabled = string.Equals(full, newFull + ".disabled", StringComparison.OrdinalIgnoreCase);
            var sameId = newOwnId is not null &&
                         string.Equals(entry.Manifest.Id, newOwnId, StringComparison.OrdinalIgnoreCase);

            if (staleDisabled || sameId || sameProject.Contains(full)) victims.Add(entry.Path);
        }

        return victims;
    }

    /// <summary>
    /// Reads the folder and the new jar, then <see cref="Select"/>s the other copies of it.
    /// </summary>
    /// <param name="folder">The content folder.</param>
    /// <param name="newJarPath">The jar just installed.</param>
    /// <param name="projectId">The new jar's store project, or null if unknown.</param>
    /// <param name="projectByPath">Which store project each existing jar is; may be null.</param>
    /// <param name="read">How to read one jar; the content tab passes its cache.</param>
    internal static IReadOnlyList<string> OthersOf(
        string folder, string newJarPath, string? projectId = null,
        IReadOnlyDictionary<string, string>? projectByPath = null,
        Func<string, ContentManifest.Manifest>? read = null)
    {
        read ??= ContentManifest.Read;
        var newOwnId = File.Exists(newJarPath) ? read(newJarPath).Id : null;
        return Select(ContentManifest.ReadAll(folder, read), newJarPath, newOwnId, projectId, projectByPath);
    }

    /// <summary>
    /// Takes every one of <paramref name="victims"/> out, or none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In two steps, because deleting one at a time could delete the first copy and then find the
    /// second held open by the running server — which left the folder with the new jar and an old
    /// one, or, once the new jar was rolled back, with no copy of the mod at all. Renaming is refused
    /// for a file in use exactly as deleting is, so every copy is renamed aside first; if any
    /// refuses, the renamed ones are put back and nothing has changed.
    /// </para>
    /// <para>
    /// Only once all of them have moved are they deleted. A delete that still fails leaves a file no
    /// loader reads, under a name <see cref="SweepLeftovers"/> clears next time round.
    /// </para>
    /// </remarks>
    /// <param name="victims">The copies to remove.</param>
    /// <param name="rollbackPath">
    /// The new jar, deleted if the old copies cannot be removed. Null when deleting it would lose the
    /// mod — when the download replaced a file of the same name.
    /// </param>
    /// <returns>The outcome, and the paths that were removed (as they were named before).</returns>
    internal static (RemoveResult Result, IReadOnlyList<string> Removed) RemoveAllOrNothing(
        IReadOnlyList<string> victims, string? rollbackPath)
    {
        var moved = new List<(string From, string To)>();

        foreach (var victim in victims)
        {
            if (!File.Exists(victim)) continue;

            var aside = victim + RemovingSuffix;
            try
            {
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(victim, aside);
                moved.Add((victim, aside));
            }
            catch
            {
                // In use: put back what already moved, so the server keeps every file it had.
                for (var i = moved.Count - 1; i >= 0; i--)
                {
                    try { File.Move(moved[i].To, moved[i].From); } catch { /* best-effort */ }
                }

                if (rollbackPath is not null)
                {
                    try { File.Delete(rollbackPath); } catch { /* best-effort */ }
                }

                return (RemoveResult.Locked, Array.Empty<string>());
            }
        }

        foreach (var (_, aside) in moved)
        {
            try { File.Delete(aside); } catch { /* swept on the next refresh */ }
        }

        return (RemoveResult.Done, moved.Select(m => m.From).ToList());
    }

    /// <summary>Deletes whatever an interrupted removal left aside.</summary>
    internal static void SweepLeftovers(string folder)
    {
        if (!Directory.Exists(folder)) return;

        foreach (var left in Directory.EnumerateFiles(folder, "*" + RemovingSuffix, SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(left); } catch { /* still held; next time */ }
        }
    }

    /// <summary>The name to show for a removed file: its own, without <c>.disabled</c>.</summary>
    internal static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;
    }
}
