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
    public async Task<string?> CreateBackupAsync(ServerConfig config, string trigger, IProgress<string>? log = null,
        CancellationToken ct = default, string? protectFromPruning = null)
    {
        var levelName = GetLevelName(config);
        var worldDir = Path.Combine(config.FolderPath, levelName);
        if (!Directory.Exists(worldDir))
            return null;

        var dir = BackupsDir(config);
        Directory.CreateDirectory(dir);
        var fileName = $"{levelName}-{DateTime.Now:yyyyMMdd-HHmmss}--{trigger}.zip";
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
    /// closed). The server is expected to already be fully stopped by the time this runs
    /// (<see cref="ServerProcessManager.StopAsync"/> waits for the real OS exit before returning),
    /// so this retry is a defense-in-depth net for the rare residual lock, not the primary fix.
    /// </summary>
    private static void CreateZipWithRetry(string worldDir, string zipPath)
    {
        try
        {
            ZipFile.CreateFromDirectory(worldDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Antivirus-style transient locks surface as either exception type depending on how
            // the scanner holds the file, so both take the retry path.
            TryDeleteZip(zipPath); // CreateFromDirectory already created a partial file before failing
            Thread.Sleep(1000);
            try
            {
                ZipFile.CreateFromDirectory(worldDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            }
            catch
            {
                // Still locked: don't leave a partial/corrupt zip in backups/ for the list to show.
                TryDeleteZip(zipPath);
                throw;
            }
        }
    }

    private static void TryDeleteZip(string zipPath)
    {
        try { File.Delete(zipPath); } catch { /* best-effort */ }
    }

    private void PruneOldBackups(ServerConfig config, string? protectFromPruning = null)
    {
        var retention = Math.Max(1, config.BackupRetention);
        var candidates = ListBackups(config);
        if (protectFromPruning is not null)
            candidates = candidates.Where(b => !string.Equals(b.FilePath, protectFromPruning, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var old in candidates.Skip(retention))
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
        var worldDir = Path.Combine(config.FolderPath, levelName);

        if (Directory.Exists(worldDir))
        {
            log?.Report(Localizer.Get("Msg_BackupSafetyNet"));
            await CreateBackupAsync(config, "before-restore", log, ct, protectFromPruning: zipPath);
        }

        log?.Report(Localizer.Get("Msg_BackupRestoring"));
        await Task.Run(() =>
        {
            if (Directory.Exists(worldDir))
                Directory.Delete(worldDir, recursive: true);
            Directory.CreateDirectory(worldDir);
            ZipFile.ExtractToDirectory(zipPath, worldDir, overwriteFiles: true);
        }, ct);

        log?.Report(Localizer.Get("Msg_BackupRestored"));
    }
}
