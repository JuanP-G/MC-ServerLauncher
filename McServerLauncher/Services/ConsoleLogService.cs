using System;
using System.IO;
using System.Linq;

namespace McServerLauncher.Services;

/// <summary>
/// Persists every server's console output to a shared, dated log file
/// (%APPDATA%/McServerLauncher/logs/launcher-yyyy-MM-dd.log) so the history survives the app being
/// closed or crashing, not just the in-memory console shown in the UI. A new file starts each day;
/// files older than <see cref="RetentionDays"/> are pruned so the folder doesn't grow forever.
/// </summary>
public sealed class ConsoleLogService
{
    /// <summary>Single shared instance: the log file is one per day for the whole app, not per server.</summary>
    public static readonly ConsoleLogService Shared = new();

    private const int RetentionDays = 14;

    private static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "logs");

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    private ConsoleLogService()
    {
        TryPruneOldLogs();
    }

    /// <summary>Appends one line, timestamped and tagged with the server's name, to today's log file.</summary>
    public void Log(string serverName, string line)
    {
        try
        {
            lock (_lock)
            {
                EnsureWriterForToday();
                _writer!.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{serverName}] {line}");
                _writer.Flush();
            }
        }
        catch
        {
            // Best-effort: a logging failure must never break the console.
        }
    }

    private void EnsureWriterForToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _writerDate == today) return;

        _writer?.Dispose();
        Directory.CreateDirectory(LogsDir);
        var path = Path.Combine(LogsDir, $"launcher-{today:yyyy-MM-dd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        _writerDate = today;
    }

    private static void TryPruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(LogsDir, "launcher-*.log")
                         .Where(f => File.GetLastWriteTime(f) < cutoff))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Best-effort: pruning failures shouldn't block logging.
        }
    }
}
