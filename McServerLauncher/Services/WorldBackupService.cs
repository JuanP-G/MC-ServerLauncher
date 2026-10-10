using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Creates and restores zip backups of a server's world folder — the "level-name" directory from
/// server.properties ("world" by default). That single folder holds every dimension, including the
/// modern layout that nests them under "dimensions/", so zipping it alone is a complete backup.
/// Backups live in "&lt;server folder&gt;/backups/"; old ones beyond the configured retention are
/// pruned after each new one.
/// </summary>
public class WorldBackupService
{
    private readonly ServerPropertiesService _properties = new();

    /// <summary>One backup zip already on disk, as the backups list shows it.</summary>
    /// <param name="FilePath">Full path, which is what a restore reads from.</param>
    /// <param name="FileName">Its name, which is also where the other fields are read from.</param>
    /// <param name="CreatedAt">The file's write time.</param>
    /// <param name="SizeBytes">Size of the zip.</param>
    /// <param name="Trigger">Why it was made ("start", "stop", "manual", "before-restore"), or "?" for a name this app didn't write.</param>
    public record BackupInfo(string FilePath, string FileName, DateTime CreatedAt, long SizeBytes, string Trigger);

    private static string BackupsDir(ServerConfig config) => Path.Combine(config.FolderPath, "backups");

    /// <summary>The world folder name (server.properties' level-name, "world" if unset).</summary>
    public string GetLevelName(ServerConfig config)
    {
        var props = _properties.Read(config.PropertiesPath);
        return props.TryGetValue("level-name", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : "world";
    }

    /// <summary>
    /// The folder <paramref name="levelName"/> points at, or null when it is not a world folder
    /// this app may zip or replace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>level-name</c> is a value from a file the app did not write — a server added from a folder
    /// someone downloaded, or a typo in the editor — and a restore deletes what it names,
    /// recursively. <c>Path.Combine</c> keeps nothing in: <c>.</c> is the server folder itself, with
    /// <c>backups/</c> and the very zip being restored inside it; <c>..</c> is the folder above; an
    /// absolute path throws the server folder away altogether.
    /// </para>
    /// <para>
    /// So the world has to resolve to a folder strictly inside the server's, and not inside
    /// <c>backups/</c>. Subfolders are fine — <c>worlds/survival</c> is a real layout.
    /// </para>
    /// </remarks>
    internal static string? WorldFolderFor(string serverFolder, string levelName)
    {
        string server, world;
        try
        {
            server = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverFolder));
            world = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(server, levelName)));
        }
        catch
        {
            return null;   // characters the platform rejects in a path: not a folder at all
        }

        var backups = Path.Combine(server, "backups");
        return IsInside(world, server) && !IsInside(world, backups) && !SamePath(world, backups)
            ? world
            : null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static bool IsInside(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);

    private static bool SamePath(string a, string b) => string.Equals(a, b, PathComparison);

    /// <summary>All backups for this server, newest first.</summary>
    public IReadOnlyList<BackupInfo> ListBackups(ServerConfig config)
    {
        var dir = BackupsDir(config);
        if (!Directory.Exists(dir)) return Array.Empty<BackupInfo>();

        return Directory.EnumerateFiles(dir, "*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.FullName, f.Name, f.LastWriteTime, f.Length, ParseTrigger(f.Name)))
            .ToList();
    }

    /// <summary>
    /// Extracts the trigger from "&lt;level&gt;-&lt;yyyyMMdd-HHmmss&gt;--&lt;trigger&gt;.zip". The double
    /// hyphen right before the trigger is the unambiguous marker: both the level name and the trigger
    /// itself (e.g. "before-restore") may contain single hyphens, so splitting on the last single "-"
    /// would cut in the wrong place.
    /// </summary>
    private static string ParseTrigger(string fileName)
    {
        var noExt = Path.GetFileNameWithoutExtension(fileName);
        var idx = noExt.LastIndexOf("--", StringComparison.Ordinal);
        return idx >= 0 && idx + 2 < noExt.Length ? noExt[(idx + 2)..] : "?";
    }

    /// <summary>
    /// Zips the world folder into backups/. No-op (returns null) if the world doesn't exist yet — a
    /// server that has never been started has nothing to back up. Prunes old backups beyond
    /// <see cref="ServerConfig.BackupRetention"/> afterward; <paramref name="protectFromPruning"/> (if
    /// given) is never deleted by that pruning, even if it would otherwise have aged out — used by
    /// <see cref="RestoreBackupAsync"/> so its own safety-net backup can never delete the very backup
    /// being restored from.
    /// </summary>
    public virtual async Task<string?> CreateBackupAsync(ServerConfig config, string trigger, IProgress<string>? log = null,
        CancellationToken ct = default, string? protectFromPruning = null)
    {
        var levelName = GetLevelName(config);
        if (WorldFolderFor(config.FolderPath, levelName) is not { } worldDir)
        {
            // Said and skipped rather than thrown: this runs before every start, and a backup that
            // cannot be made is not a reason to keep the server down.
            log?.Report(string.Format(Localizer.Get("Msg_ErrorFmt"),
                string.Format(Localizer.Get("Msg_BackupBadLevelNameFmt"), levelName)));
            return null;
        }

        if (!Directory.Exists(worldDir))
            return null;

        var dir = BackupsDir(config);
        Directory.CreateDirectory(dir);
        // The folder's own name, not level-name: "worlds/survival" would otherwise put a slash in
        // the file name and the zip in a folder that does not exist.
        var fileName = $"{Path.GetFileName(worldDir)}-{DateTime.Now:yyyyMMdd-HHmmss}--{trigger}.zip";
        var zipPath = Path.Combine(dir, fileName);

        log?.Report(string.Format(Localizer.Get("Msg_BackupCreatingFmt"), levelName));
        // Region files (.mca) are already internally compressed, so re-deflating them at the
        // "Optimal" level burns CPU for little gain; "Fastest" keeps backups quick without giving
        // up much size.
        await Task.Run(() => CreateZipWithRetry(worldDir, zipPath), ct);

        var sizeMb = new FileInfo(zipPath).Length / (1024.0 * 1024.0);
        log?.Report(string.Format(Localizer.Get("Msg_BackupCreatedFmt"), sizeMb.ToString("0.#")));

        PruneOldBackups(config, protectFromPruning);
        return zipPath;
    }

    /// <summary>
    /// Zips <paramref name="worldDir"/>, retrying once after a short pause if a file inside it is
    /// still transiently locked (e.g. antivirus scanning a region file the server process just
    /// closed).
    /// </summary>
    private static void CreateZipWithRetry(string worldDir, string zipPath)
    {
        try
        {
            ZipWorld(worldDir, zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Antivirus-style transient locks surface as either exception type depending on how
            // the scanner holds the file, so both take the retry path.
            TryDeleteZip(zipPath); // CreateFromDirectory already created a partial file before failing
            Thread.Sleep(1000);
            try
            {
                ZipWorld(worldDir, zipPath);
            }
            catch
            {
                // Still locked: don't leave a partial/corrupt zip in backups/ for the list to show.
                TryDeleteZip(zipPath);
                throw;
            }
        }
    }

    /// <summary>The running server's claim on the world folder, never part of a backup.</summary>
    private const string SessionLock = "session.lock";

    /// <summary>
    /// Zips the world folder the way <see cref="ZipFile.CreateFromDirectory(string, string)"/> would,
    /// but readable while the server still has it open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backups by the clock run with the server up. <c>save-off</c> stops it writing, but it
    /// keeps files open for writing all the same — <c>session.lock</c> for as long as it runs, and
    /// the region files it has touched. <c>CreateFromDirectory</c> opens each file letting others
    /// only read, which Windows refuses while another process can write to it, so the very first
    /// file it met failed the whole backup.
    /// </para>
    /// <para>
    /// Each file is opened letting others read, write and delete instead: nothing is written while
    /// saving is off, so what is read is what was saved. <c>session.lock</c> is skipped altogether —
    /// the server also locks its bytes, so even that would not read it, and it is not the world:
    /// the server writes a new one every time it starts, including after a restore.
    /// </para>
    /// </remarks>
    private static void ZipWorld(string worldDir, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var root = new DirectoryInfo(worldDir);

        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(worldDir, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');

            if (entry is DirectoryInfo dir)
            {
                // Kept only when empty, as CreateFromDirectory does: a folder with files in it comes
                // back with them.
                if (!dir.EnumerateFileSystemInfos().Any())
                    zip.CreateEntry(name + "/");
                continue;
            }

            if (string.Equals(name, SessionLock, StringComparison.OrdinalIgnoreCase))
                continue;

            var zipped = zip.CreateEntry(name, CompressionLevel.Fastest);
            zipped.LastWriteTime = entry.LastWriteTime;
            using var source = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var target = zipped.Open();
            source.CopyTo(target);
        }
    }

    private static void TryDeleteZip(string zipPath)
    {
        try { File.Delete(zipPath); } catch { /* best-effort */ }
    }

    /// <summary>A backup the app made on its own: before a start, after a stop, or by the clock.</summary>
    internal static bool IsAutomatic(string trigger) => trigger is "start" or "stop" or "auto";

    /// <summary>A backup somebody asked for, directly or by restoring another one.</summary>
    internal static bool IsTheUsers(string trigger) => trigger is "manual" or "before-restore";

    /// <summary>
    /// Deletes what is past the retention, counting the automatic backups and the user's own
    /// separately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One shared count stopped working the day backups started happening by the clock: a server
    /// left running overnight would fill the whole allowance with hourly copies, and the backup
    /// somebody took by hand before trying something — the one they were sure they still had —
    /// would be the first to go.
    /// </para>
    /// <para>
    /// Zips this app did not write (trigger "?") are never deleted at all. They are in the folder
    /// because somebody put them there.
    /// </para>
    /// </remarks>
    private void PruneOldBackups(ServerConfig config, string? protectFromPruning = null)
    {
        var candidates = ListBackups(config);
        if (protectFromPruning is not null)
            candidates = candidates.Where(b => !string.Equals(b.FilePath, protectFromPruning, StringComparison.OrdinalIgnoreCase)).ToList();

        DeletePast(candidates.Where(b => IsAutomatic(b.Trigger)), config.BackupRetention);
        DeletePast(candidates.Where(b => IsTheUsers(b.Trigger)), config.ManualBackupRetention);
    }

    private static void DeletePast(IEnumerable<BackupInfo> newestFirst, int keep)
    {
        foreach (var old in newestFirst.Skip(Math.Max(1, keep)))
        {
            try { File.Delete(old.FilePath); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Restores a backup, replacing the current world folder entirely. Makes a safety backup of the
    /// CURRENT state first (trigger "before-restore") so the restore itself can be undone. That
    /// safety backup's own retention pruning is not allowed to delete <paramref name="zipPath"/> —
    /// otherwise a tight retention could prune the very backup being restored from before it's read.
    /// </summary>
    public async Task RestoreBackupAsync(ServerConfig config, string zipPath, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var levelName = GetLevelName(config);

        // Checked before anything is touched: what follows deletes this folder recursively, and
        // with level-name set to "." that used to be the whole server, backups — and the zip about
        // to be read — included.
        var worldDir = WorldFolderFor(config.FolderPath, levelName)
            ?? throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_BackupBadLevelNameFmt"), levelName));

        if (Directory.Exists(worldDir))
        {
            log?.Report(Localizer.Get("Msg_BackupSafetyNet"));
            await CreateBackupAsync(config, "before-restore", log, ct, protectFromPruning: zipPath);
        }

        log?.Report(Localizer.Get("Msg_BackupRestoring"));
        await Task.Run(() => ReplaceWorld(worldDir, zipPath), ct);

        log?.Report(Localizer.Get("Msg_BackupRestored"));
    }

    /// <summary>Puts the contents of <paramref name="zipPath"/> where <paramref name="worldDir"/> is.</summary>
    /// <remarks>
    /// <para>
    /// The zip is unpacked beside the world first, and only a complete copy takes its place, by
    /// renaming. This used to delete the world and then unpack into the empty folder, so a damaged
    /// zip or a full disk left half a world behind; the safety backup was there, but nothing said it
    /// was needed. Now a failed unpack leaves the world exactly as it was.
    /// </para>
    /// <para>
    /// Both temporary folders are siblings of the world, so the renames stay on one volume, and
    /// leftovers of a restore interrupted by a crash are cleared before starting.
    /// </para>
    /// </remarks>
    internal static void ReplaceWorld(string worldDir, string zipPath)
    {
        var incoming = worldDir + ".restoring";
        var outgoing = worldDir + ".replaced";
        DeleteFolder(incoming);
        DeleteFolder(outgoing);

        try
        {
            Directory.CreateDirectory(incoming);
            ZipFile.ExtractToDirectory(zipPath, incoming, overwriteFiles: true);
        }
        catch
        {
            DeleteFolder(incoming);
            throw;   // the world was never touched
        }

        if (Directory.Exists(worldDir)) Directory.Move(worldDir, outgoing);
        try
        {
            Directory.Move(incoming, worldDir);
        }
        catch
        {
            // Put the old world back rather than leave none at all.
            if (!Directory.Exists(worldDir) && Directory.Exists(outgoing)) Directory.Move(outgoing, worldDir);
            throw;
        }

        // Best-effort: a file held open by an antivirus can keep this one around until next time.
        try { DeleteFolder(outgoing); } catch { /* cleared by the next restore */ }
    }

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
