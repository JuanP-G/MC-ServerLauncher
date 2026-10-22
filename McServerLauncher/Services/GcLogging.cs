using System.IO;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Asks the JVM for a GC log, which is how the app learns how much heap a server really keeps alive.
/// </summary>
/// <remarks>
/// <para>
/// The numbers a process shows from outside say how much memory it holds, not how much it needs:
/// the heap is full of garbage between collections. Only the collector knows what survived, and it
/// says so in the log, after every collection.
/// </para>
/// <para>
/// The path is relative to the server's folder, where the process runs: <c>-Xlog</c> uses colons to
/// separate its own parts, so a drive letter would break it. A small rotating pair of files keeps it
/// at 16 MB at most. It sits in a hidden folder the world backup never reads.
/// </para>
/// <para>
/// A log that cannot be opened stops the JVM from starting, so the flag is given only after the
/// folder has been made and could be written to, and never on a Java that does not know it
/// (<c>-Xlog</c> came with Java 9; OpenJ9 has its own, different option).
/// </para>
/// </remarks>
public static class GcLogging
{
    /// <summary>The hidden folder inside a server's folder that holds the log.</summary>
    public const string FolderName = ".mcserverlauncher-memory";

    /// <summary>The file name the JVM writes, with the rotated copies next to it.</summary>
    public const string FileName = "gc.log";

    /// <summary>The first Java release the app asks for a log: the long-term one after <c>-Xlog</c> appeared.</summary>
    public const int MinimumJava = 11;

    /// <summary>The log's path for a server.</summary>
    public static string LogPath(string serverFolder) => Path.Combine(serverFolder, FolderName, FileName);

    /// <summary>
    /// The flag that makes the JVM write the log, or null when it should not be asked: an older Java,
    /// OpenJ9, or arguments that already configure logging and would be overridden.
    /// </summary>
    public static string? Flag(JavaService.JavaRuntime java, string? extraArgs)
    {
        if (java.IsOpenJ9 || java.Major < MinimumJava) return null;
        if (JvmArgs.Split(extraArgs).Any(t => t.StartsWith("-Xlog", StringComparison.Ordinal))) return null;
        return $"-Xlog:gc:file={FolderName}/{FileName}:uptime,level,tags:filecount=2,filesize=8M";
    }

    /// <summary>
    /// Makes the folder and clears the last run's files, so the reader never mistakes them for this
    /// run's. Returns false when it cannot, which means the flag must not be given.
    /// </summary>
    public static bool Prepare(string serverFolder)
    {
        try
        {
            var dir = Path.Combine(serverFolder, FolderName);
            Directory.CreateDirectory(dir);
            foreach (var file in Directory.EnumerateFiles(dir, FileName + "*"))
                File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The flags to add to this start's command line: the log, or nothing.</summary>
    public static IReadOnlyList<string> FlagsFor(ServerConfig config, JavaService.JavaRuntime java)
    {
        if (Flag(java, config.ExtraJvmArgs) is not { } flag || !Prepare(config.FolderPath)) return [];
        return [flag];
    }
}
