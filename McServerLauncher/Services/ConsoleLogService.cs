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
/// <remarks>
/// <para>
/// Pruning used to happen once, when the app started. The app is made to live in the tray for
/// weeks, so on the machines where the logs pile up most it simply never ran; it now also runs each
/// time the day's file changes.
/// </para>
/// <para>
/// And a day's file has a ceiling, <see cref="DefaultMaxBytesPerDay"/>. A server stuck printing the
/// same error in a loop used to write until the disk was full. Past the ceiling one line says so and
/// nothing more is written until the next day; the console on screen is not affected.
/// </para>
/// </remarks>
public sealed class ConsoleLogService : IDisposable
{
    /// <summary>Single shared instance: the log file is one per day for the whole app, not per server.</summary>
    public static readonly ConsoleLogService Shared = new(DefaultLogsDir, () => DateTime.Now, DefaultMaxBytesPerDay);

    private const int RetentionDays = 14;

    /// <summary>How much one day's file may hold. Far beyond any healthy server's day.</summary>
    internal const long DefaultMaxBytesPerDay = 50L * 1024 * 1024;

    private static string DefaultLogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "logs");

    private readonly string _logsDir;
    private readonly Func<DateTime> _now;
    private readonly long _maxBytesPerDay;

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private long _writtenToday;
    private bool _full;

    // Flushing to disk once per interval instead of per line (EFI-5): a verbose server used to
    // pay a synchronous disk flush for every console line. Losing at most the last couple of
    // seconds of log if the whole app dies abruptly is an acceptable trade for this use; the
    // clean-shutdown path calls Flush() explicitly.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private readonly System.Threading.Timer _flushTimer;

    /// <param name="logsDir">Where the daily files go.</param>
    /// <param name="now">The local clock, which decides the day.</param>
    /// <param name="maxBytesPerDay">The ceiling on one day's file.</param>
    internal ConsoleLogService(string logsDir, Func<DateTime> now, long maxBytesPerDay)
    {
        _logsDir = logsDir;
        _now = now;
        _maxBytesPerDay = maxBytesPerDay;
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    /// <summary>Appends one line, timestamped and tagged with the server's name, to today's log file.</summary>
    public void Log(string serverName, string line)
    {
        try
        {
            lock (_lock)
            {
                var now = _now();
                EnsureWriterForToday(now);
                if (_full) return;

                var entry = $"[{now:HH:mm:ss}] [{serverName}] {line}";
                // Characters, not encoded bytes: close enough for a ceiling, and free.
                _writtenToday += entry.Length + Environment.NewLine.Length;

                if (_writtenToday > _maxBytesPerDay)
                {
                    _full = true;
                    _writer!.WriteLine($"[{now:HH:mm:ss}] [log] This day's log reached its size limit; " +
                                       "nothing more is written until tomorrow.");
                    return;
                }

                _writer!.WriteLine(entry);
            }
        }
        catch
        {
            // Best-effort: a logging failure must never break the console.
        }
    }

    /// <summary>Forces buffered lines to disk. Called periodically and on clean shutdown.</summary>
    public void Flush()
    {
        try
        {
            lock (_lock)
                _writer?.Flush();
        }
        catch
        {
            // Best-effort, same as Log.
        }
    }

    private void EnsureWriterForToday(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (_writer is not null && _writerDate == today) return;

        _writer?.Dispose();
        _writer = null;

        // A new day — or the first line of this run — is when old files go.
        TryPruneOldLogs(now);

        Directory.CreateDirectory(_logsDir);
        var path = Path.Combine(_logsDir, $"launcher-{today:yyyy-MM-dd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream);
        _writerDate = today;

        // A restart the same day carries on from what that day's file already holds.
        _writtenToday = stream.Length;
        _full = _writtenToday > _maxBytesPerDay;
    }

    private void TryPruneOldLogs(DateTime now)
    {
        try
        {
            if (!Directory.Exists(_logsDir)) return;
            var cutoff = now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_logsDir, "launcher-*.log")
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

    public void Dispose()
    {
        _flushTimer.Dispose();
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { /* going away */ }
            _writer = null;
        }
    }
}
