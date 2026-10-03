using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Fills a new player history from the logs the server already kept, once.
/// </summary>
/// <remarks>
/// <para>
/// So the Players tab does not start empty on a server that has been running for months. The server
/// keeps its own logs in <c>logs/</c>: one gzipped file per start or per day
/// (<c>2026-09-18-1.log.gz</c>) and the current one, <c>latest.log</c>. Every line goes through the
/// same <see cref="PlayerEventParser"/> as the live console.
/// </para>
/// <para>
/// A log line carries only the time of day, so the date comes from the file. A gzipped log's name
/// starts with the day it began. <c>latest.log</c> has no date in its name: its last write is the
/// day it ended, and every time the clock goes backwards between two lines a midnight was crossed,
/// so the day it began is that many days earlier.
/// </para>
/// <para>
/// <c>latest.log</c> is skipped while the server runs: the live console is recording those very
/// lines at the same moment.
/// </para>
/// </remarks>
public static partial class PlayerLogImporter
{
    /// <summary>Imports the server's logs into <paramref name="store"/>, unless that was done already.</summary>
    /// <returns>How many events were read (duplicates included, those are dropped by the store).</returns>
    public static int ImportOnce(string serverFolder, PlayerHistoryStore store, bool serverRunning,
        CancellationToken ct = default)
    {
        if (store.Imported) return 0;

        var logs = Path.Combine(serverFolder, "logs");
        var count = 0;

        store.BeginImport();
        try
        {
            if (Directory.Exists(logs))
            {
                var archived = Directory.EnumerateFiles(logs, "*.log.gz")
                    .Select(f => (File: f, Name: ArchiveName().Match(Path.GetFileName(f))))
                    .Where(x => x.Name.Success)
                    .OrderBy(x => x.Name.Groups[1].Value, StringComparer.Ordinal)
                    .ThenBy(x => int.Parse(x.Name.Groups[2].Value, CultureInfo.InvariantCulture));

                foreach (var (file, name) in archived)
                {
                    ct.ThrowIfCancellationRequested();
                    var day = DateTime.ParseExact(name.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    count += ImportLines(ReadGzip(file), day, store);
                }

                var latest = Path.Combine(logs, "latest.log");
                if (!serverRunning && File.Exists(latest))
                {
                    var lines = ReadText(latest);
                    var endDay = File.GetLastWriteTime(latest).Date;
                    count += ImportLines(lines, endDay.AddDays(-Midnights(lines)), store);
                }
            }
        }
        finally
        {
            // Even when nothing was there, or it failed half-way: whatever was read stays, and the
            // import is not attempted again on every start.
            store.EndImport();
        }

        return count;
    }

    /// <summary>Records the player events in one log, whose first line is on <paramref name="startDay"/>.</summary>
    internal static int ImportLines(IReadOnlyList<string> lines, DateTime startDay, PlayerHistoryStore store)
    {
        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var day = startDay.Date;
        TimeSpan? previous = null;
        var count = 0;

        foreach (var line in lines)
        {
            if (TimeOf(line) is not { } time) continue;
            if (previous is { } p && time < p) day = day.AddDays(1);
            previous = time;

            var local = DateTime.SpecifyKind(day + time, DateTimeKind.Local);

            if (PlayerEventParser.UuidOf(line) is { } uuid)
            {
                store.RecordUuid(uuid.Player, uuid.Uuid);
                continue;
            }

            if (PlayerEventParser.Parse(line, online) is not { } e) continue;

            if (e.Kind == PlayerEventKind.Join) online.Add(e.Player);
            else if (e.Kind == PlayerEventKind.Leave) online.Remove(e.Player);

            store.Record(e, local.ToUniversalTime());
            count++;
        }

        // A log that ends with players still on it is a server that stopped or crashed without
        // saying so. Their sessions end at the last thing each of them did — and only theirs: a
        // player on the live server right now must not have their session closed by an old file.
        store.CloseOpenSessions(online, atUtc: null);
        return count;
    }

    /// <summary>How many times the clock goes backwards in a log: the midnights it crosses.</summary>
    internal static int Midnights(IReadOnlyList<string> lines)
    {
        var count = 0;
        TimeSpan? previous = null;
        foreach (var line in lines)
        {
            if (TimeOf(line) is not { } time) continue;
            if (previous is { } p && time < p) count++;
            previous = time;
        }
        return count;
    }

    /// <summary>The time at the start of a log line, <c>[12:34:56]</c> or Paper's <c>[12:34:56 INFO]</c>.</summary>
    internal static TimeSpan? TimeOf(string line)
    {
        var m = LineTime().Match(line);
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return h < 24 && min < 60 && s < 60 ? new TimeSpan(h, min, s) : null;
    }

    private static List<string> ReadGzip(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return ReadAll(reader);
        }
        catch
        {
            return new List<string>();   // a damaged archive is skipped, not fatal
        }
    }

    private static List<string> ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return ReadAll(reader);
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> ReadAll(StreamReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})-(\d+)\.log\.gz$")]
    private static partial Regex ArchiveName();

    [GeneratedRegex(@"^\[(\d{2}):(\d{2}):(\d{2})[\] ]")]
    private static partial Regex LineTime();
}
