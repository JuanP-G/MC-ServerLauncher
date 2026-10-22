using System.Collections.Concurrent;
using System.IO;

namespace McServerLauncher.Services;

/// <summary>The memory side of a server's history: its latest runs, oldest first.</summary>
public sealed class MemoryHistory
{
    /// <summary>The file format, in case it ever has to change.</summary>
    public int Version { get; set; } = 1;

    public List<MemorySession> Sessions { get; set; } = new();
}

/// <summary>Loads and saves one server's <see cref="MemoryHistory"/>.</summary>
/// <remarks>
/// Under <c>%APPDATA%\McServerLauncher\memory\&lt;serverId&gt;\history.json</c>, beside the content
/// ledger and for the same reason: it is about the app's view of the server, so it neither travels
/// with the world nor fills its backups. Only the latest <see cref="MaxSessions"/> runs are kept;
/// what the app learns from them is about how the server is used now.
/// </remarks>
public sealed class MemoryHistoryStore
{
    /// <summary>How many runs are kept.</summary>
    public const int MaxSessions = 30;

    /// <summary>Where every server's history lives. Settable so tests can point it at a temp folder.</summary>
    public static string RootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "memory");

    private static readonly ConcurrentDictionary<string,
        MemoryHistoryStore> Open = new(StringComparer.Ordinal);

    /// <summary>The store for a server, shared by everything that asks for it.</summary>
    public static MemoryHistoryStore For(string serverId) =>
        Open.GetOrAdd(Path.Combine(RootDirectory, serverId), dir => new MemoryHistoryStore(dir));

    /// <summary>
    /// Deletes the history of servers that are no longer in the app. A removed server's id is never
    /// reused, so what is left would only pile up for ever.
    /// </summary>
    public static void PruneOrphans(IEnumerable<string> liveServerIds)
    {
        try
        {
            if (!Directory.Exists(RootDirectory)) return;
            var live = liveServerIds.ToHashSet(StringComparer.Ordinal);
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                if (live.Contains(Path.GetFileName(dir))) continue;
                Open.TryRemove(dir, out _);
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best-effort: what is left is cleared next time */ }
    }

    private readonly string _path;
    private readonly object _lock = new();
    private readonly MemoryHistory _history;

    public MemoryHistoryStore(string directory)
    {
        _path = Path.Combine(directory, "history.json");

        // A missing or unreadable file is an empty history: nothing in it is worth refusing to start for.
        var (loaded, _) = AtomicJsonFile.Load<MemoryHistory>(_path);
        _history = loaded ?? new MemoryHistory();
        _history.Sessions ??= new();
    }

    /// <summary>The kept runs, oldest first. A copy: safe to read while another run is added.</summary>
    public IReadOnlyList<MemorySession> Sessions
    {
        get { lock (_lock) return _history.Sessions.ToList(); }
    }

    /// <summary>Adds a finished run, drops the oldest beyond <see cref="MaxSessions"/> and saves.</summary>
    public void Add(MemorySession session)
    {
        lock (_lock)
        {
            _history.Sessions.Add(session);
            if (_history.Sessions.Count > MaxSessions)
                _history.Sessions.RemoveRange(0, _history.Sessions.Count - MaxSessions);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicJsonFile.Write(_path, _history);
        }
    }
}
