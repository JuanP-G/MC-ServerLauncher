using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Creates and restores zip backups of a server's world folder — the "level-name" directory from
/// server.properties ("world" by default) — together with the Nether and the End when the server
/// keeps them beside it. Backups live in "&lt;server folder&gt;/backups/"; old ones beyond the
/// configured retention are pruned after each new one.
/// </summary>
/// <remarks>
/// Vanilla and the modded loaders keep every dimension inside the world folder (under
/// <c>DIM-1</c>, <c>DIM1</c> or <c>dimensions/</c>), so that folder alone is the whole world. Paper
/// and Purpur, like every Bukkit server, keep them in sibling folders, <c>world_nether</c> and
/// <c>world_the_end</c>. Those used to be left out: every backup of a Paper server lacked them,
/// and restoring one put an old overworld beside today's Nether and End. See
/// <see cref="DimensionSuffixes"/>.
/// </remarks>
public class WorldBackupService
{
    private readonly ServerPropertiesService _properties = new();

    /// <summary>One backup zip already on disk, as the backups list shows it.</summary>
    /// <param name="FilePath">Full path, which is what a restore reads from.</param>
    /// <param name="FileName">Its name, which is also where the other fields are read from.</param>
    /// <param name="CreatedAt">The file's write time.</param>
    /// <param name="SizeBytes">Size of the zip.</param>
    /// <param name="Trigger">Why it was made ("start", "stop", "manual", "before-restore"), or "?" for a name
    /// this app didn't write.</param>
    public record BackupInfo(string FilePath, string FileName, DateTime CreatedAt, long SizeBytes,
        string Trigger);

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

    /// <summary>The sibling folders a Bukkit server keeps its other dimensions in.</summary>
    /// <remarks>
    /// Found on disk rather than decided by the server's type: a server converted away from Paper
    /// still has them, and a vanilla one never does, so "it is there" is the answer that is right in
    /// both cases.
    /// </remarks>
    internal static readonly string[] DimensionSuffixes = { "_nether", "_the_end" };

    /// <summary>
    /// Where the sibling dimensions go inside a zip: a folder no Minecraft writes, so the world's own
    /// files stay at the root and every zip made before this still restores the same.
    /// </summary>
    internal const string DimensionsRoot = ".mcserverlauncher-dimensions";

    /// <summary>The sibling dimension folders of <paramref name="levelName"/> on disk.</summary>
    internal static IReadOnlyList<(string Suffix, string Folder)> DimensionsBeside(string serverFolder,
        string levelName)
    {
        var found = new List<(string, string)>();
        foreach (var suffix in DimensionSuffixes)
        {
            if (WorldFolderFor(serverFolder, levelName + suffix) is { } folder && Directory.Exists(folder))
                found.Add((suffix, folder));
        }
        return found;
    }

    private static bool IsInside(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, FilePaths.Comparison);

    private static bool SamePath(string a, string b) => string.Equals(a, b, FilePaths.Comparison);

    /// <summary>All backups for this server, newest first.</summary>
    /// <remarks>
    /// Only names ending exactly in <c>.zip</c>: a copy still being written is a <c>.zip.tmp</c>
    /// (see <see cref="PartialSuffix"/>), and Windows' <c>*.zip</c> pattern also matches longer
    /// extensions that merely start with it.
    /// </remarks>
    public IReadOnlyList<BackupInfo> ListBackups(ServerConfig config)
    {
        var dir = BackupsDir(config);
        if (!Directory.Exists(dir)) return Array.Empty<BackupInfo>();

        return Directory.EnumerateFiles(dir, "*.zip")
            .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
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
    public virtual async Task<string?> CreateBackupAsync(ServerConfig config, string trigger,
        IProgress<string>? log = null,
        string? protectFromPruning = null, CancellationToken ct = default)
    {
        var levelName = GetLevelName(config);
        if (WorldFolderFor(config.FolderPath, levelName) is not { } worldDir)
        {
            // Said and skipped rather than thrown: this runs before every start, and a backup that
            // cannot be made is not a reason to keep the server down.
            log?.Report(Localizer.Format("Msg_ErrorFmt",
                Localizer.Format("Msg_BackupBadLevelNameFmt", levelName)));
            return null;
        }

        if (!Directory.Exists(worldDir))
            return null;

        ct.ThrowIfCancellationRequested();
        var dir = BackupsDir(config);
        Directory.CreateDirectory(dir);
        SweepPartials(dir);
        // The folder's own name, not level-name: "worlds/survival" would otherwise put a slash in
        // the file name and the zip in a folder that does not exist.
        var fileName = $"{Path.GetFileName(worldDir)}-{DateTime.Now:yyyyMMdd-HHmmss}--{trigger}.zip";
        var zipPath = Path.Combine(dir, fileName);

        log?.Report(Localizer.Format("Msg_BackupCreatingFmt", levelName));

        // Taken before the copy, not after: by then a running server may already have written to
        // the world again, and the record would describe a world this zip does not hold — one the
        // next start would find "up to date" and not copy. Taken first, any change made meanwhile
        // only means the next start makes one copy too many, which is the safe side to be wrong on.
        var dimensions = DimensionsBeside(config.FolderPath, levelName);
        var fingerprint = await Task.Run(() => Fingerprint(worldDir, dimensions), ct);
        await Task.Run(() => CreateZipWithRetry(worldDir, dimensions, zipPath, ct), ct);

        var sizeMb = new FileInfo(zipPath).Length / (1024.0 * 1024.0);
        log?.Report(Localizer.Format("Msg_BackupCreatedFmt", sizeMb.ToString("0.#")));

        WriteState(config, new BackupState(fileName, fingerprint));
        PruneOldBackups(config, protectFromPruning);
        return zipPath;
    }

    /// <summary>What a backup is called while it is being written.</summary>
    /// <remarks>
    /// The zip used to be written under its final name, so a copy cut short — the app closed or
    /// updated halfway, a failure the retry below did not catch — stayed in the list as a backup:
    /// one that counted towards the retention, could push a good one out, and failed when restored.
    /// Now it only gets its <c>.zip</c> name once it is complete.
    /// </remarks>
    internal const string PartialSuffix = ".tmp";

    /// <summary>Deletes copies an earlier run left half-written.</summary>
    /// <remarks>
    /// Safe because only one copy of a server's world is ever made at a time (the server's backup
    /// gate) and only one instance of the app runs.
    /// </remarks>
    private static void SweepPartials(string backupsDir)
    {
        foreach (var partial in Directory.EnumerateFiles(backupsDir, "*.zip" + PartialSuffix))
            FileCleanup.TryDelete(partial);
    }

    /// <summary>
    /// Zips <paramref name="worldDir"/> into <paramref name="zipPath"/>, retrying once after a short
    /// pause if a file inside it is still transiently locked (e.g. antivirus scanning a region file
    /// the server process just closed).
    /// </summary>
    /// <remarks>
    /// Whatever goes wrong — a lock that outlasts the retry, a cancellation, anything else — the
    /// half-written file is deleted, and <paramref name="zipPath"/> never exists until the copy is
    /// whole.
    /// </remarks>
    private void CreateZipWithRetry(string worldDir, IReadOnlyList<(string Suffix, string Folder)> dimensions,
        string zipPath, CancellationToken ct)
    {
        var partial = zipPath + PartialSuffix;
        try
        {
            try
            {
                ZipWorld(worldDir, dimensions, partial, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Antivirus-style transient locks surface as either exception type depending on how
                // the scanner holds the file, so both take the retry path.
                FileCleanup.TryDelete(partial);
                Thread.Sleep(1000);
                ZipWorld(worldDir, dimensions, partial, ct);
            }

            ct.ThrowIfCancellationRequested();   // cancelled during the last file: still not a backup
            File.Move(partial, zipPath);
        }
        catch
        {
            FileCleanup.TryDelete(partial);
            throw;
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
    private void ZipWorld(string worldDir, IReadOnlyList<(string Suffix, string Folder)> dimensions,
        string zipPath, CancellationToken ct)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        ZipFolder(zip, worldDir, "", ct);
        foreach (var (suffix, folder) in dimensions)
            ZipFolder(zip, folder, DimensionsRoot + "/" + suffix + "/", ct);
    }

    /// <summary>Adds one folder of the world to the zip, under <paramref name="prefix"/>.</summary>
    private void ZipFolder(ZipArchive zip, string folder, string prefix, CancellationToken ct)
    {
        foreach (var (name, entry) in WorldEntries(folder))
        {
            ct.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo dir)
            {
                // Kept only when empty, as CreateFromDirectory does: a folder with files in it comes
                // back with them.
                if (!dir.EnumerateFileSystemInfos().Any())
                    zip.CreateEntry(prefix + name + "/");
                continue;
            }

            // Region files (.mca) are already internally compressed, so re-deflating them at the
            // "Optimal" level burns CPU for little gain; "Fastest" keeps backups quick without
            // giving up much size.
            var zipped = zip.CreateEntry(prefix + name, CompressionLevel.Fastest);
            zipped.LastWriteTime = ZipTime(entry.LastWriteTime);
            using var source = OpenForCopy(entry.FullName);
            using var target = zipped.Open();
            source.CopyTo(target);
        }
    }

    /// <summary>
    /// Every file and folder of one world folder that belongs in a backup, by its name relative to
    /// the folder (with <c>/</c>).
    /// </summary>
    /// <remarks>
    /// Without <c>session.lock</c>, and without a <see cref="DimensionsRoot"/> folder an older version
    /// of the app may have unpacked into the world when it restored a newer zip.
    /// </remarks>
    private static IEnumerable<(string Name, FileSystemInfo Entry)> WorldEntries(string folder)
    {
        var all = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories);
        foreach (var entry in all)
        {
            var name = Path.GetRelativePath(folder, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(name, SessionLock, StringComparison.OrdinalIgnoreCase)) continue;
            if (name == DimensionsRoot || name.StartsWith(DimensionsRoot + "/", StringComparison.Ordinal))
                continue;
            yield return (name, entry);
        }
    }

    /// <summary>A file's time as a zip can hold it: between 1980 and 2107.</summary>
    /// <remarks>
    /// The zip format stores dates from 1980 to 2107, and setting one outside that throws.
    /// <see cref="ZipFile.CreateFromDirectory(string, string)"/>, which this replaced, quietly used
    /// 1980 instead; the hand-written copy did not, so a single file dated 1970 — copied out of a
    /// tar, a container or a Nix store — failed every backup of the world, and with it every start.
    /// </remarks>
    internal static DateTime ZipTime(DateTime local) =>
        local.Year < 1980 ? new DateTime(1980, 1, 1)
        : local.Year > 2107 ? new DateTime(2107, 12, 31, 23, 59, 58)
        : local;

    /// <summary>Opens one file of the world for the zip, letting the running server keep it open.</summary>
    /// <remarks>Virtual so a test can make one file fail halfway through a copy.</remarks>
    internal virtual Stream OpenForCopy(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Which backup the world on disk is a copy of, as far as anybody playing could tell.
    /// </summary>
    /// <param name="Zip">The backup's file name in <c>backups/</c>.</param>
    /// <param name="Fingerprint">The world folder as it was then; see <see cref="Fingerprint"/>.</param>
    internal record BackupState(string Zip, string Fingerprint);

    /// <summary>
    /// Beside the zips it names, so it travels with them; the list only shows <c>*.zip</c>.
    /// </summary>
    private static string StatePath(ServerConfig config)
        => Path.Combine(BackupsDir(config), "last-backup.json");

    /// <summary>
    /// Whether the world on disk is still the one the last backup copied — so a start or a stop can
    /// skip making another copy of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server that sleeps when empty and wakes when somebody presses Join used to leave two backups
    /// per cycle — one on the way down, one on the way up — even when nobody came in. Enough of those
    /// and the retention deleted every copy somebody had actually played in.
    /// </para>
    /// <para>
    /// False whenever there is any doubt: no record, a record naming a zip that is no longer there
    /// (somebody cleared the folder), a world that is missing, or one whose files differ in any way
    /// from what was recorded — including a <c>level-name</c> that now points somewhere else.
    /// </para>
    /// </remarks>
    public virtual bool IsUpToDate(ServerConfig config)
    {
        try
        {
            if (ReadState(config) is not { } state) return false;
            if (!File.Exists(Path.Combine(BackupsDir(config), state.Zip))) return false;
            var levelName = GetLevelName(config);
            if (WorldFolderFor(config.FolderPath, levelName) is not { } worldDir
                || !Directory.Exists(worldDir)) return false;
            return state.Fingerprint == Fingerprint(worldDir, DimensionsBeside(config.FolderPath, levelName));
        }
        catch
        {
            return false;   // can't tell: make the copy
        }
    }

    /// <summary>
    /// Records that the world as it is now counts as the last backup, without copying it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a server that ran with nobody playing and nothing typed that changes the world. Minecraft
    /// still writes on the way out — <c>level.dat</c> keeps the time it was last saved — so its files
    /// no longer match the backup, but nothing anybody would want back is in the difference. Does
    /// nothing when there is no backup to point at, so it can never stand in for a missing one.
    /// </para>
    /// <para>
    /// Nor when the server takes commands over RCON (<c>enable-rcon=true</c>): a <c>/fill</c> sent
    /// that way changes the world with nobody joining and nothing typed here, and only the copy can
    /// tell. Other changes nobody joins to make — a farm in the spawn chunks, which tick with nobody
    /// near, or a plugin on a timer — are small enough to wait for the next time somebody plays.
    /// </para>
    /// </remarks>
    public virtual void MarkUnchanged(ServerConfig config)
    {
        try
        {
            if (AcceptsRemoteCommands(config)) return;
            if (ReadState(config) is not { } state) return;
            if (!File.Exists(Path.Combine(BackupsDir(config), state.Zip))) return;
            var levelName = GetLevelName(config);
            if (WorldFolderFor(config.FolderPath, levelName) is not { } worldDir
                || !Directory.Exists(worldDir)) return;
            var fingerprint = Fingerprint(worldDir, DimensionsBeside(config.FolderPath, levelName));
            WriteState(config, state with { Fingerprint = fingerprint });
        }
        catch (Exception ex)
        {
            Safe.Note("Backup", "the world's fingerprint could not be saved, the next start copies again", ex);
            // Best-effort: the next start just makes the copy it would have made anyway.
        }
    }

    private bool AcceptsRemoteCommands(ServerConfig config) =>
        _properties.Read(config.PropertiesPath).TryGetValue("enable-rcon", out var rcon)
        && string.Equals(rcon.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static BackupState? ReadState(ServerConfig config)
    {
        var path = StatePath(config);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<BackupState>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static void WriteState(ServerConfig config, BackupState state)
    {
        try
        {
            var path = StatePath(config);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Safe.Note("Backup", "the backup state file could not be written, one extra copy may be made", ex);
            // Best-effort: without it the next start or stop makes one copy too many, nothing worse.
        }
    }

    /// <summary>
    /// A hash of every file in the world: where it is, how big, and when it was last written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contents are not read — a world can be gigabytes, and this runs on every start and stop.
    /// Anything that writes a file changes its time, which is enough. <c>session.lock</c> is left
    /// out, as it is from the zip: the server rewrites it on every start. The folders' own paths are
    /// part of it, so pointing <c>level-name</c> at another world is a change too.
    /// </para>
    /// <para>
    /// With no sibling dimensions the text hashed is exactly what it was before they were counted, so
    /// only a Paper or Purpur server makes one extra copy after updating the app.
    /// </para>
    /// </remarks>
    internal static string Fingerprint(string worldDir,
        IReadOnlyList<(string Suffix, string Folder)>? dimensions = null)
    {
        dimensions ??= Array.Empty<(string, string)>();
        var lines = WorldEntries(worldDir).Select(e => (e.Name, e.Entry))
            .Concat(dimensions.SelectMany(d => WorldEntries(d.Folder)
                .Select(e => (Name: DimensionsRoot + "/" + d.Suffix + "/" + e.Name, e.Entry))))
            .Where(e => e.Entry is FileInfo)
            .Select(e => $"{e.Name}|{((FileInfo)e.Entry).Length}|{e.Entry.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal);

        var folders = dimensions.Select(d => d.Folder).Prepend(worldDir).Select(Path.GetFullPath);
        var text = string.Join("\n", folders) + "\n" + string.Join("\n", lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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
            candidates = candidates.Where(b => !string.Equals(b.FilePath, protectFromPruning,
                StringComparison.OrdinalIgnoreCase)).ToList();

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
            ?? throw new UserFacingException(
                Localizer.Format("Msg_BackupBadLevelNameFmt", levelName));

        if (Directory.Exists(worldDir))
        {
            log?.Report(Localizer.Get("Msg_BackupSafetyNet"));
            await CreateBackupAsync(config, "before-restore", log, protectFromPruning: zipPath, ct: ct);
        }

        log?.Report(Localizer.Get("Msg_BackupRestoring"));
        await Task.Run(() => ReplaceWorld(worldDir, zipPath), ct);

        log?.Report(Localizer.Get("Msg_BackupRestored"));
    }

    /// <summary>
    /// Puts the contents of <paramref name="zipPath"/> where <paramref name="worldDir"/> is.
    /// </summary>
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
    /// <para>
    /// The Nether and the End a Paper server keeps beside the world (see <see cref="DimensionSuffixes"/>)
    /// are swapped with it as one unit: if any part cannot be put in place, every part already swapped
    /// goes back, so the world never ends up from one moment and its dimensions from another. A zip
    /// without them — made before they were backed up — leaves the ones on disk as they are.
    /// </para>
    /// </remarks>
    internal static void ReplaceWorld(string worldDir, string zipPath)
    {
        if (!ZipHoldsDimensions(zipPath))
        {
            ReplaceFolder(worldDir, zipPath);
            return;
        }

        var units = DimensionSuffixes.Select(suffix => worldDir + suffix).Prepend(worldDir).ToList();
        foreach (var unit in units)
        {
            DeleteFolder(unit + ".restoring");
            DeleteFolder(unit + ".replaced");
        }

        var swapped = new List<string>();
        try
        {
            var present = UnpackWithDimensions(worldDir, zipPath);
            SwapIn(present, swapped);
        }
        catch
        {
            RollBack(units, swapped);
            throw;
        }
        finally
        {
            foreach (var unit in units)
            {
                try { DeleteFolder(unit + ".restoring"); } catch { /* cleared by the next restore */ }
            }
        }

        // Best-effort: a file held open by an antivirus can keep one around until next time.
        foreach (var unit in swapped)
        {
            try { DeleteFolder(unit + ".replaced"); } catch { /* cleared by the next restore */ }
        }
    }

    /// <summary>
    /// Unpacks the zip into <c>&lt;world&gt;.restoring</c> and moves its dimensions out beside it, each
    /// into its own <c>.restoring</c> folder, as Paper expects them. Returns the units to swap in: the
    /// world, and each dimension the zip carried.
    /// </summary>
    private static List<string> UnpackWithDimensions(string worldDir, string zipPath)
    {
        var incoming = worldDir + ".restoring";
        Directory.CreateDirectory(incoming);
        ZipFile.ExtractToDirectory(zipPath, incoming, overwriteFiles: true);

        // The dimensions come out of the world's folder to sit beside it, as Paper expects them.
        var dimensions = Path.Combine(incoming, DimensionsRoot);
        var present = new List<string> { worldDir };
        foreach (var suffix in DimensionSuffixes)
        {
            var packed = Path.Combine(dimensions, suffix);
            if (!Directory.Exists(packed)) continue;
            Directory.Move(packed, worldDir + suffix + ".restoring");
            present.Add(worldDir + suffix);
        }
        DeleteFolder(dimensions);
        return present;
    }

    /// <summary>
    /// Puts each unpacked unit in place of the one on disk, which is kept as <c>.replaced</c>.
    /// <paramref name="swapped"/> grows as it goes, so a failure halfway knows what to undo.
    /// </summary>
    private static void SwapIn(List<string> present, List<string> swapped)
    {
        foreach (var unit in present)
        {
            if (Directory.Exists(unit)) Directory.Move(unit, unit + ".replaced");
            Directory.Move(unit + ".restoring", unit);
            swapped.Add(unit);
        }
    }

    /// <summary>Every part back as it was, newest first; a part that never moved is still in place.</summary>
    private static void RollBack(List<string> units, List<string> swapped)
    {
        foreach (var unit in units.Where(u => !swapped.Contains(u)))
        {
            try
            {
                if (!Directory.Exists(unit) && Directory.Exists(unit + ".replaced"))
                    Directory.Move(unit + ".replaced", unit);
            }
            catch { /* best-effort: the before-restore backup still has it */ }
        }
        for (var i = swapped.Count - 1; i >= 0; i--)
        {
            try
            {
                DeleteFolder(swapped[i]);
                if (Directory.Exists(swapped[i] + ".replaced"))
                    Directory.Move(swapped[i] + ".replaced", swapped[i]);
            }
            catch { /* best-effort: the before-restore backup still has it */ }
        }
    }

    /// <summary>Whether a zip carries sibling dimensions, as only the newer ones do.</summary>
    private static bool ZipHoldsDimensions(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var root = DimensionsRoot + "/";
            return zip.Entries.Any(e => e.FullName.StartsWith(root, StringComparison.Ordinal));
        }
        catch
        {
            return false;   // not a zip at all: ReplaceFolder fails on it, leaving the world as it was
        }
    }

    /// <summary>Puts <paramref name="zipPath"/> where <paramref name="worldDir"/> is, on its own.</summary>
    private static void ReplaceFolder(string worldDir, string zipPath)
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

        try
        {
            if (Directory.Exists(worldDir)) Directory.Move(worldDir, outgoing);
            try
            {
                Directory.Move(incoming, worldDir);
            }
            catch
            {
                // Put the old world back rather than leave none at all.
                if (!Directory.Exists(worldDir) && Directory.Exists(outgoing)) Directory.Move(outgoing,
                    worldDir);
                throw;
            }
        }
        finally
        {
            // A whole second copy of the world, if moving the old one aside failed (a file in use).
            try { DeleteFolder(incoming); } catch { /* cleared by the next restore */ }
        }

        // Best-effort: a file held open by an antivirus can keep this one around until next time.
        try { DeleteFolder(outgoing); } catch { /* cleared by the next restore */ }
    }

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
