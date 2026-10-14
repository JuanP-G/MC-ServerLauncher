using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Whether a world was last saved by a newer Minecraft than the server about to open it.
/// </summary>
/// <remarks>
/// <para>
/// Minecraft upgrades worlds, never the other way. Opened by an older version, every chunk whose
/// format it does not know loads as air ("Recoverable errors when loading section … Not a map"),
/// and the next save writes that air over the blocks. A world moved from 26.3 back to 26.1 lost its
/// spawn that way, with the app letting the start go ahead.
/// </para>
/// <para>
/// The version that last saved it is in <c>level.dat</c> (<c>Data.Version.Name</c>). It is compared
/// with the server's only when both are plain release numbers; a snapshot, a pre-release or a name
/// that cannot be read says "can't tell", and that never stops a start.
/// </para>
/// </remarks>
public static class WorldVersionCheck
{
    /// <summary>The version that last saved the world in <paramref name="worldDir"/>, or null.</summary>
    public static string? SavedBy(string worldDir) =>
        NbtReader.ReadString(Path.Combine(worldDir, "level.dat"), "Data", "Version", "Name");

    /// <summary>True only when both are release numbers and <paramref name="world"/> is the later one.</summary>
    public static bool IsNewer(string? world, string? server) =>
        Release(world) is { } w && Release(server) is { } s && w > s;

    /// <summary>
    /// The version that saved <paramref name="config"/>'s world, when it is newer than the server's;
    /// null otherwise, including when there is no world yet or it cannot be read.
    /// </summary>
    public static string? NewerThanServer(ServerConfig config)
    {
        try
        {
            var backups = new WorldBackupService();
            if (WorldBackupService.WorldFolderFor(config.FolderPath, backups.GetLevelName(config)) is not { } world)
                return null;

            var savedBy = SavedBy(world);
            return IsNewer(savedBy, config.GameVersion) ? savedBy : null;
        }
        catch
        {
            return null;   // can't tell: never a reason to keep a server down
        }
    }

    /// <summary>A backup whose world the server's version can open.</summary>
    /// <param name="FilePath">The zip.</param>
    /// <param name="CreatedAt">When it was made, as the backups list shows it.</param>
    /// <param name="Version">The Minecraft version that had saved the world it holds.</param>
    public sealed record CompatibleBackup(string FilePath, DateTime CreatedAt, string Version);

    /// <summary>
    /// The newest backup made with a version no newer than the server's, or null.
    /// </summary>
    /// <remarks>
    /// Going up a version and back down is undone this way: the backups taken before the upgrade
    /// still hold the world as the older version wrote it. Each zip carries its own
    /// <c>level.dat</c>, so the version is read from the backup itself rather than guessed from its
    /// date. One whose version cannot be read, or is a snapshot, is not offered.
    /// </remarks>
    public static CompatibleBackup? BackupFor(ServerConfig config)
    {
        if (Release(config.GameVersion) is null) return null;

        try
        {
            foreach (var backup in new WorldBackupService().ListBackups(config))
            {
                var version = SavedByBackup(backup.FilePath);
                if (Release(version) is not null && !IsNewer(version, config.GameVersion))
                    return new CompatibleBackup(backup.FilePath, backup.CreatedAt, version!);
            }
        }
        catch
        {
            // A folder that cannot be listed offers nothing; the red strip still says why.
        }
        return null;
    }

    /// <summary>The version that had saved the world inside a backup zip, or null.</summary>
    public static string? SavedByBackup(string zipPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            if (zip.GetEntry("level.dat") is not { } entry) return null;
            using var stream = entry.Open();
            return NbtReader.ReadString(stream, "Data", "Version", "Name");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"26.3" or "1.21.4" as a comparable version; null for anything else.</summary>
    private static Version? Release(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (!trimmed.All(c => char.IsAsciiDigit(c) || c == '.')) return null;
        if (!Version.TryParse(trimmed.Contains('.') ? trimmed : trimmed + ".0", out var v)) return null;

        // "1.21" and "1.21.0" are the same release; Version would put the first one before.
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
