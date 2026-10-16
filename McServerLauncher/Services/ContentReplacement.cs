using System.Collections.Generic;
using System.IO;
using System.Linq;

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
    /// <param name="folder">Every jar in the content folder, from
    /// <see cref="ContentManifest.ReadAll"/>.</param>
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
            ? ReplacedBy(projectByPath, projectId, newJarPath)
                .Select(Path.GetFullPath)
                .ToHashSet(FilePaths.Comparer)
            : new HashSet<string>(FilePaths.Comparer);

        var victims = new List<string>();
        foreach (var entry in folder)
        {
            var full = Path.GetFullPath(entry.Path);
            if (string.Equals(full, newFull, FilePaths.Comparison)) continue;

            var staleDisabled = string.Equals(full, newFull + ".disabled", FilePaths.Comparison);
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
    /// mod — when the download replaced a file of the same name. Kept as well when an old copy could
    /// not be put back: it is then the only copy of the mod left in place.
    /// </param>
    /// <param name="move">How a file is renamed; a test passes one that fails.</param>
    /// <returns>The outcome, and the paths that were removed (as they were named before).</returns>
    internal static (RemoveResult Result, IReadOnlyList<string> Removed) RemoveAllOrNothing(
        IReadOnlyList<string> victims, string? rollbackPath, Action<string, string>? move = null)
    {
        move ??= (from, to) => File.Move(from, to);
        var moved = new List<(string From, string To)>();

        foreach (var victim in victims)
        {
            if (!File.Exists(victim)) continue;

            var aside = victim + RemovingSuffix;
            try
            {
                if (File.Exists(aside)) File.Delete(aside);
                move(victim, aside);
                moved.Add((victim, aside));
            }
            catch
            {
                // In use: put back what already moved, so the server keeps every file it had.
                var allPutBack = true;
                for (var i = moved.Count - 1; i >= 0; i--)
                {
                    try { move(moved[i].To, moved[i].From); }
                    catch { allPutBack = false; }
                }

                // Only when everything is back where it was. A copy left aside is swept next time,
                // and deleting the new jar too used to leave the server with no copy of the mod.
                if (rollbackPath is not null && allPutBack)
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

        foreach (var left in Directory.EnumerateFiles(folder, "*" + RemovingSuffix,
            SearchOption.TopDirectoryOnly))
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

    /// <summary>What happened to one update.</summary>
    internal enum UpdateOutcome
    {
        /// <summary>The new version is in place and the old one is gone.</summary>
        Updated,

        /// <summary>The old jar is in use — the server is running. Nothing was changed.</summary>
        NeedsStop,

        /// <summary>Something else went wrong. Nothing was changed.</summary>
        Failed
    }

    /// <summary>
    /// The installed jars a new download of <paramref name="projectId"/> at <paramref name="newPath"/>
    /// replaces: the same project, under any other file name.
    /// </summary>
    internal static IReadOnlyList<string> ReplacedBy(
        IReadOnlyDictionary<string, string> projectByPath, string projectId, string newPath) =>
        projectByPath
            .Where(kv => string.Equals(kv.Value, projectId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .Where(path => !FilePaths.Same(path, newPath))
            .ToList();

    /// <summary>
    /// Everything after the download of an update: the old version and every other copy of the mod
    /// out, and the new one switched on or off the way the mod was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the download so it can be tested on a folder of fake jars, with no network.
    /// </para>
    /// <para>
    /// A disabled mod stays disabled — unless another copy of it was switched on, because then the
    /// server was running that copy, and the update is of the mod it was actually using.
    /// </para>
    /// </remarks>
    /// <param name="modsFolder">The content folder.</param>
    /// <param name="downloadedPath">Where the new version was just written, under its enabled name.</param>
    /// <param name="oldPath">The row's own file, <c>.disabled</c> included if it was off.</param>
    /// <param name="wasDisabled">Whether the row was switched off.</param>
    /// <param name="downloadReplacedAFile">
    /// True when the download overwrote a file of the same name. Then the new jar is the only copy
    /// left and must not be rolled back if the others cannot be removed.
    /// </param>
    /// <param name="projectId">The update's store project, or null.</param>
    /// <param name="projectByPath">Which store project each jar is; may be null.</param>
    /// <param name="read">How to read one jar's manifest.</param>
    internal static (UpdateOutcome Outcome, IReadOnlyList<string> Removed) ApplyDownloadedUpdate(
        string modsFolder, string downloadedPath, string oldPath, bool wasDisabled,
        bool downloadReplacedAFile,
        string? projectId, IReadOnlyDictionary<string, string>? projectByPath,
        Func<string, ContentManifest.Manifest>? read = null)
    {
        var victims = OthersOf(modsFolder, downloadedPath, projectId, projectByPath, read).ToList();

        // The row's own file always goes, even when nothing else ties it to the new one — a jar that
        // declares no id, and a store that did not answer.
        var oldIsNew = FilePaths.Same(oldPath, downloadedPath);
        if (!oldIsNew && File.Exists(oldPath) && !victims.Contains(oldPath, FilePaths.Comparer))
            victims.Add(oldPath);

        // The download itself counts when it overwrote a file: that was an enabled copy by the same
        // name, the one the server was using — and left out, the update switched the mod off.
        var anotherCopyWasOn = downloadReplacedAFile
                               || victims.Any(v =>
                                   !v.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));

        // Old jar in use (server running): keeping both would load two versions of the mod, so the
        // new one is rolled back and the user is told to stop the server first.
        var (result, removed) = RemoveAllOrNothing(
            victims, downloadReplacedAFile ? null : downloadedPath);
        if (result == RemoveResult.Locked)
            return (UpdateOutcome.NeedsStop, Array.Empty<string>());

        if (wasDisabled && !anotherCopyWasOn)
        {
            var disabledPath = downloadedPath + ".disabled";
            if (File.Exists(disabledPath)) File.Delete(disabledPath);
            File.Move(downloadedPath, disabledPath);
        }

        return (UpdateOutcome.Updated, removed);
    }
}
