using System.IO;
using System.Runtime.InteropServices;

namespace McServerLauncher.Services;

/// <summary>
/// Writes down what went wrong when something escapes every handler, so a crash leaves a trace.
/// </summary>
/// <remarks>
/// <para>
/// There was no global handler at all. An exception nobody caught closed the app without a word,
/// and the servers it had started went on running with no window to stop them — the next start
/// found their ports busy and offered to kill them, which does not save the world. Now each one
/// leaves a <c>crash-*.log</c> beside the console logs, with what failed, where, and on what system:
/// the file to attach to a bug report.
/// </para>
/// <para>
/// Only the newest <see cref="Keep"/> are kept; a fault that repeats should not fill the folder.
/// </para>
/// </remarks>
public static class CrashLog
{
    /// <summary>How many crash files are kept.</summary>
    internal const int Keep = 20;

    /// <summary>Where they go. Settable so a test can point it somewhere else.</summary>
    internal static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "logs");

    /// <summary>Records <paramref name="error"/>. Returns the file, or null if it could not be written.</summary>
    /// <param name="origin">Which handler caught it, so the file says how far it got.</param>
    /// <param name="error">What was thrown.</param>
    public static string? Write(string origin, Exception error)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var now = DateTime.Now;
            var path = Path.Combine(Folder, $"crash-{now:yyyyMMdd-HHmmss-fff}.log");
            var version = typeof(CrashLog).Assembly.GetName().Version;

            File.WriteAllText(path,
                $"{now:O}  {origin}{Environment.NewLine}" +
                $"MC Server Launcher {version}  ·  {RuntimeInformation.OSDescription} " +
                $"({RuntimeInformation.OSArchitecture})  ·  .NET {Environment.Version}{Environment.NewLine}" +
                Environment.NewLine + error);

            Prune();
            return path;
        }
        catch
        {
            return null;   // nothing left to report a failure to report to
        }
    }

    private static void Prune()
    {
        foreach (var old in Directory.EnumerateFiles(Folder, "crash-*.log")
                     .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                     .Skip(Keep))
        {
            try { File.Delete(old); } catch { /* the next crash tries again */ }
        }
    }
}
