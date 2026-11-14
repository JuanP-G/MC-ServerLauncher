using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What a server's folder takes on disk, in total and by the parts people ask about.</summary>
/// <param name="Total">Every file under the folder.</param>
/// <param name="World">The world folder (<c>level-name</c>) and its Bukkit siblings <c>_nether</c> and <c>_the_end</c>.</param>
/// <param name="Backups">The <c>backups</c> folder, where the launcher keeps its copies of the world.</param>
/// <param name="Content">The <c>mods</c> or <c>plugins</c> folder, whichever the server type uses.</param>
/// <param name="Logs">The <c>logs</c> and <c>crash-reports</c> folders.</param>
/// <param name="BackupCount">How many finished copies are in <c>backups</c>.</param>
public sealed record ServerDiskUsage(long Total, long World, long Backups, long Content, long Logs, int BackupCount)
{
    /// <summary>Everything else: the server jar, libraries, configuration, caches.</summary>
    public long Other => Math.Max(0, Total - World - Backups - Content - Logs);
}

/// <summary>
/// Measures a server's folder. Slow on a big world, so it is meant to run off the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Files are counted by their length, which can be read while the server holds them open: there is
/// no need to stop the server to know what it takes.
/// </para>
/// <para>
/// Links and junctions are not followed. A folder linked from somewhere else would be counted
/// twice, or forever if it points back up, and what it holds is not this server's anyway.
/// Folders that cannot be read are skipped rather than failing the whole count.
/// </para>
/// </remarks>
public static class ServerDiskUsageService
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Not the default (Hidden | System): a hidden file still takes room.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly string[] LogFolders = { "logs", "crash-reports" };

    /// <summary>Measures <paramref name="config"/>'s folder, or returns null when it is not there.</summary>
    public static ServerDiskUsage? Measure(ServerConfig config, CancellationToken ct = default)
    {
        var root = config.FolderPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

        long total = 0, world = 0, backups = 0, content = 0, logs = 0;

        var worldFolders = WorldFolders(root, config.Type).ToHashSet(FilePaths.Comparer);
        var contentFolder = Full(Path.Combine(root, ServerTypeCatalog.ContentFolder(config.Type)));
        var backupsFolder = Full(Path.Combine(root, "backups"));
        var logFolders = LogFolders.Select(f => Full(Path.Combine(root, f))).ToHashSet(FilePaths.Comparer);

        // Top level first: each folder there belongs to one part, so one walk per folder answers
        // both "how much in total" and "how much of it is the world".
        foreach (var file in Safe(() => new DirectoryInfo(root).EnumerateFiles("*", TopLevel)))
            total += file.Length;

        foreach (var dir in Safe(() => new DirectoryInfo(root).EnumerateDirectories("*", TopLevel)))
        {
            ct.ThrowIfCancellationRequested();
            var size = SizeOf(dir, ct);
            total += size;

            var path = Full(dir.FullName);
            if (worldFolders.Contains(path)) world += size;
            else if (FilePaths.Comparer.Equals(path, backupsFolder)) backups += size;
            else if (FilePaths.Comparer.Equals(path, contentFolder)) content += size;
            else if (logFolders.Contains(path)) logs += size;
        }

        var backupCount = Safe(() => Directory.EnumerateFiles(backupsFolder, "*.zip"))
            .Count(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return new ServerDiskUsage(total, world, backups, content, logs, backupCount);
    }

    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>The world's folders: the one <c>level-name</c> names, and its dimension siblings if any.</summary>
    private static IEnumerable<string> WorldFolders(string root, ServerType type)
    {
        string levelName;
        try
        {
            // A native server (Pumpkin) has no server.properties: its level name is in its own file.
            var props = ServerTypeCatalog.IsNative(type)
                ? PumpkinConfig.ReadAsProperties(root)
                : new ServerPropertiesService().Read(Path.Combine(root, "server.properties"));
            levelName = ServerPropertiesService.GetLevelName(props);
        }
        catch
        {
            levelName = ServerPropertiesService.DefaultLevelName;
        }

        if (WorldBackupService.WorldFolderFor(root, levelName) is { } world) yield return Full(world);
        foreach (var (_, folder) in WorldBackupService.DimensionsBeside(root, levelName))
            yield return Full(folder);
    }

    private static long SizeOf(DirectoryInfo dir, CancellationToken ct)
    {
        long size = 0;
        foreach (var file in Safe(() => dir.EnumerateFiles("*", Walk)))
        {
            ct.ThrowIfCancellationRequested();
            try { size += file.Length; }
            catch (IOException) { }   // gone between being listed and being read
        }
        return size;
    }

    /// <summary>Lists lazily and stops quietly if the folder goes away or refuses halfway through.</summary>
    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> list)
    {
        IEnumerator<T>? items = null;
        try { items = list().GetEnumerator(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (items is null) yield break;

        using (items)
        {
            while (true)
            {
                var more = false;
                try { more = items.MoveNext(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (!more) yield break;
                yield return items.Current;
            }
        }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
