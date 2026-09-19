using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What the history knows about one player, all in one place.</summary>
/// <remarks>Times are UTC. Mutable because it is the index's own storage, not a message.</remarks>
public sealed class PlayerRecord
{
    public string Name { get; set; } = "";
    public string? Uuid { get; set; }
    public DateTime? FirstSeen { get; set; }
    public DateTime? LastJoin { get; set; }
    public DateTime? LastLeave { get; set; }

    /// <summary>When the session in progress started; null when the player is not on the server.</summary>
    public DateTime? OpenSince { get; set; }

    /// <summary>The newest event recorded for this player.</summary>
    public DateTime? LastEventAt { get; set; }

    public int Sessions { get; set; }
    public long SecondsPlayed { get; set; }
    public int Messages { get; set; }

    /// <summary>Lines in the events file, so it can be compacted without being counted each time.</summary>
    public int EventCount { get; set; }

    /// <summary>The last time this player was seen: leaving, joining, or doing anything at all.</summary>
    [JsonIgnore]
    public DateTime? LastSeen => Max(Max(LastLeave, LastJoin), LastEventAt);

    internal static DateTime? Max(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    public PlayerRecord Copy() => (PlayerRecord)MemberwiseClone();
}

/// <summary>One event as it is stored and shown.</summary>
public sealed record StoredEvent(DateTime At, PlayerEventKind Kind, string? Text);

/// <summary>The app-wide history settings, set from AppSettings at startup and by the settings dialog.</summary>
/// <remarks>The same shape as <see cref="ConsolePreferences"/>, for the same reason.</remarks>
public static class PlayerHistoryPreferences
{
    public static PlayerHistorySettings Current { get; set; } = new();
}

/// <summary>
/// One server's player history: who came, when, for how long, and what they did and said.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>%APPDATA%\McServerLauncher\players\&lt;serverId&gt;\</c>, outside the server's
/// folder, so it neither travels with the world nor fills its backups. <c>index.json</c> holds one
/// summary per player; <c>events/&lt;name&gt;.jsonl</c> holds that player's events, one short JSON
/// object per line.
/// </para>
/// <para>
/// <strong>Kept small on purpose.</strong> Events files are only ever appended to, and compacted in
/// one go when they pass the per-player limit by a quarter — the same trick the console uses to
/// avoid rewriting on every line. Old events and long-gone players are dropped at start-up. The IP
/// address the server logs at every login is never read, so it cannot be stored.
/// </para>
/// <para>
/// Every public member takes the one lock: the live console records from the UI thread, the import
/// from a background one, and the settings dialog can clear everything at any moment.
/// </para>
/// </remarks>
public sealed partial class PlayerHistoryStore : IDisposable
{
    /// <summary>Where every server's history lives.</summary>
    public static string RootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "players");

    private static readonly ConcurrentDictionary<string, PlayerHistoryStore> Open = new(StringComparer.Ordinal);

    /// <summary>The store for a server, shared by everything that asks for it.</summary>
    public static PlayerHistoryStore For(string serverId) =>
        Open.GetOrAdd(serverId, id => new PlayerHistoryStore(Path.Combine(RootDirectory, id)));

    private readonly object _lock = new();
    private readonly string _directory;
    private readonly Func<PlayerHistorySettings> _settings;
    private readonly Func<DateTime> _utcNow;
    private readonly Timer _saveTimer;
    private Index _index;
    private bool _dirty;

    // Only while importing: the events already on disk, per player, so the same line is not added twice.
    private Dictionary<string, HashSet<string>>? _importSeen;

    public PlayerHistoryStore(string directory, Func<PlayerHistorySettings>? settings = null, Func<DateTime>? utcNow = null)
    {
        _directory = directory;
        _settings = settings ?? (() => PlayerHistoryPreferences.Current.Clamped());
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _index = LoadIndex();
        _saveTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private string IndexPath => Path.Combine(_directory, "index.json");
    private string EventsPath(string key) => Path.Combine(_directory, "events", key + ".jsonl");

    // --- Recording ---

    /// <summary>Records something a player did at <paramref name="atUtc"/>.</summary>
    public void Record(PlayerEvent e, DateTime atUtc)
    {
        if (!IsName(e.Player)) return;

        lock (_lock)
        {
            var settings = _settings();
            if (!settings.Enabled) return;
            if (e.Kind == PlayerEventKind.Chat && !settings.RecordChat) return;
            if (atUtc < _utcNow() - TimeSpan.FromDays(settings.RetentionDays)) return;

            var key = Key(e.Player);
            var text = e.Text is { Length: > MaxText } longText ? longText[..MaxText] : e.Text;
            var line = Serialize(atUtc, e.Kind, text);

            if (_importSeen is not null)
            {
                if (!_importSeen.TryGetValue(key, out var seen))
                    _importSeen[key] = seen = ReadLines(key).ToHashSet(StringComparer.Ordinal);
                if (!seen.Add(line)) return;
            }

            var rec = GetOrAdd(key, e.Player);
            Apply(rec, e.Kind, atUtc);

            Directory.CreateDirectory(Path.GetDirectoryName(EventsPath(key))!);
            File.AppendAllText(EventsPath(key), line + "\n", Utf8);
            rec.EventCount++;
            if (rec.EventCount > settings.MaxEventsPerPlayer * 5 / 4) Compact(key, rec, settings.MaxEventsPerPlayer, null);

            _dirty = true;
        }
    }

    /// <summary>Remembers a player's UUID, which is what the server's own statistics are filed under.</summary>
    public void RecordUuid(string player, string uuid)
    {
        if (!IsName(player)) return;
        lock (_lock)
        {
            if (!_settings().Enabled) return;
            var rec = GetOrAdd(Key(player), player);
            if (rec.Uuid == uuid) return;
            rec.Uuid = uuid;
            _dirty = true;
        }
    }

    /// <summary>
    /// Ends every session still open: the server stopped or crashed without saying who left.
    /// </summary>
    /// <param name="atUtc">When they ended; null means each player's own last event, for a log that just stops.</param>
    public void CloseOpenSessions(DateTime? atUtc) => CloseOpenSessions(null, atUtc);

    /// <summary>The same, for only these players; null means everyone.</summary>
    public void CloseOpenSessions(IEnumerable<string>? players, DateTime? atUtc)
    {
        lock (_lock)
        {
            var only = players?.Where(IsName).Select(Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, rec) in _index.Players.Where(p => p.Value.OpenSince is not null).ToList())
            {
                if (only is not null && !only.Contains(key)) continue;
                var end = atUtc ?? rec.LastEventAt ?? rec.OpenSince!.Value;
                AddPlayed(rec, rec.OpenSince!.Value, end);
                rec.LastLeave = PlayerRecord.Max(rec.LastLeave, end);
                rec.OpenSince = null;
                _dirty = true;
            }
        }
        Flush();
    }

    private static void Apply(PlayerRecord rec, PlayerEventKind kind, DateTime at)
    {
        if (rec.FirstSeen is null || at < rec.FirstSeen) rec.FirstSeen = at;

        switch (kind)
        {
            case PlayerEventKind.Join:
                // A join with a session still open means the leave was never seen (a crash, a log cut
                // short). Count it up to the last thing that player did, not up to now.
                if (rec.OpenSince is { } open) AddPlayed(rec, open, rec.LastEventAt ?? open);
                rec.OpenSince = at;
                rec.Sessions++;
                rec.LastJoin = PlayerRecord.Max(rec.LastJoin, at);
                break;
            case PlayerEventKind.Leave:
                if (rec.OpenSince is { } start) AddPlayed(rec, start, at);
                rec.OpenSince = null;
                rec.LastLeave = PlayerRecord.Max(rec.LastLeave, at);
                break;
            case PlayerEventKind.Chat:
                rec.Messages++;
                break;
        }

        rec.LastEventAt = PlayerRecord.Max(rec.LastEventAt, at);
    }

    private static void AddPlayed(PlayerRecord rec, DateTime from, DateTime to)
    {
        if (to > from) rec.SecondsPlayed += (long)(to - from).TotalSeconds;
    }

    // --- Importing old logs ---

    /// <summary>Whether this server's old logs have already been imported.</summary>
    public bool Imported
    {
        get { lock (_lock) return _index.Imported; }
    }

    /// <summary>From here until <see cref="EndImport"/>, a line already stored is not stored again.</summary>
    public void BeginImport()
    {
        lock (_lock) _importSeen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    }

    /// <summary>Marks the import done, so it never runs again for this server.</summary>
    public void EndImport()
    {
        lock (_lock)
        {
            _importSeen = null;
            _index.Imported = true;
            _dirty = true;
        }
        Flush();
    }

    // --- Reading ---

    /// <summary>A copy of every player's summary.</summary>
    public IReadOnlyList<PlayerRecord> Players()
    {
        lock (_lock) return _index.Players.Values.Select(r => r.Copy()).ToList();
    }

    /// <summary>A copy of one player's summary, or null.</summary>
    public PlayerRecord? Get(string player)
    {
        if (!IsName(player)) return null;
        lock (_lock) return _index.Players.TryGetValue(Key(player), out var r) ? r.Copy() : null;
    }

    /// <summary>One player's events, newest first.</summary>
    /// <remarks>
    /// Sorted rather than read backwards: an import can add older events after newer live ones, and
    /// a file this small (a few hundred lines at most) costs nothing to sort.
    /// </remarks>
    public IReadOnlyList<StoredEvent> Events(string player)
    {
        if (!IsName(player)) return Array.Empty<StoredEvent>();
        lock (_lock)
        {
            return ReadLines(Key(player))
                .Select(Deserialize)
                .OfType<StoredEvent>()
                .OrderByDescending(e => e.At)
                .ToList();
        }
    }

    // --- Forgetting ---

    /// <summary>Forgets one player completely.</summary>
    public void Clear(string player)
    {
        if (!IsName(player)) return;
        lock (_lock)
        {
            var key = Key(player);
            _index.Players.Remove(key);
            TryDelete(EventsPath(key));
            _dirty = true;
        }
        Flush();
    }

    /// <summary>Forgets every player of this server. Imported logs are not imported again.</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _index = new Index { Imported = true };
            try
            {
                var events = Path.Combine(_directory, "events");
                if (Directory.Exists(events)) Directory.Delete(events, recursive: true);
            }
            catch { /* a file in use: whatever is left is dropped by the next prune */ }
            _dirty = true;
        }
        Flush();
    }

    /// <summary>
    /// Drops what the settings no longer allow: events past the retention, players not seen within
    /// it, and anything over the per-player limit.
    /// </summary>
    public void Prune()
    {
        lock (_lock)
        {
            var settings = _settings();
            var cutoff = _utcNow() - TimeSpan.FromDays(settings.RetentionDays);

            foreach (var (key, rec) in _index.Players.ToList())
            {
                if ((rec.LastSeen ?? rec.FirstSeen ?? DateTime.MinValue) < cutoff && rec.OpenSince is null)
                {
                    _index.Players.Remove(key);
                    TryDelete(EventsPath(key));
                    _dirty = true;
                    continue;
                }
                Compact(key, rec, settings.MaxEventsPerPlayer, cutoff);
            }
        }
        Flush();
    }

    /// <summary>Rewrites a player's events keeping the newest <paramref name="max"/>, none older than the cutoff.</summary>
    private void Compact(string key, PlayerRecord rec, int max, DateTime? cutoff)
    {
        var lines = ReadLines(key).ToList();
        var kept = lines
            .Select(l => (Line: l, Event: Deserialize(l)))
            .Where(x => x.Event is not null && (cutoff is null || x.Event.At >= cutoff))
            .OrderBy(x => x.Event!.At)
            .Select(x => x.Line)
            .ToList();
        if (kept.Count > max) kept = kept.Skip(kept.Count - max).ToList();

        if (kept.Count != lines.Count)
        {
            if (kept.Count == 0) TryDelete(EventsPath(key));
            else File.WriteAllText(EventsPath(key), string.Join("\n", kept) + "\n", Utf8);
            _dirty = true;
        }
        rec.EventCount = kept.Count;
    }

    // --- Disk ---

    /// <summary>Writes the index if anything changed. Called on a timer, on stop and on exit.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(_directory);
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_index, IndexJson), Utf8);
                File.Move(tmp, IndexPath, overwrite: true);
                _dirty = false;
            }
            catch
            {
                // Best-effort, like the console log: tried again on the next tick.
            }
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    /// <summary>Writes every open store's index. Called when the app exits.</summary>
    public static void FlushAll()
    {
        foreach (var store in Open.Values) store.Flush();
    }

    /// <summary>How much every server's history takes on disk, in bytes.</summary>
    public static long SizeOnDisk()
    {
        try
        {
            return Directory.Exists(RootDirectory)
                ? new DirectoryInfo(RootDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Forgets every player of every server.</summary>
    public static void ClearEverything()
    {
        foreach (var store in Open.Values) store.ClearAll();
        try
        {
            if (!Directory.Exists(RootDirectory)) return;
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
                if (!Open.ContainsKey(Path.GetFileName(dir)))
                    Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Deletes the history of servers that are no longer in the app and have not been touched within
    /// the retention period. A server removed and added back straight away keeps nothing either way —
    /// it gets a new id — so there is no reason to keep these for ever.
    /// </summary>
    public static void PruneOrphans(IEnumerable<string> liveServerIds, DateTime utcNow, int retentionDays)
    {
        try
        {
            if (!Directory.Exists(RootDirectory)) return;
            var live = liveServerIds.ToHashSet(StringComparer.Ordinal);
            var cutoff = utcNow - TimeSpan.FromDays(retentionDays);
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                if (live.Contains(Path.GetFileName(dir))) continue;
                var newest = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                if (newest < cutoff) Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best-effort */ }
    }

    // --- Helpers ---

    private const int MaxText = 256;   // Minecraft's own chat limit

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = false };

    private sealed class Index
    {
        public int Version { get; set; } = 1;
        public bool Imported { get; set; }
        public Dictionary<string, PlayerRecord> Players { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record Line(
        [property: JsonPropertyName("t")] long T,
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("m")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? M);

    private static readonly JsonSerializerOptions LineJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Serialize(DateTime atUtc, PlayerEventKind kind, string? text) =>
        JsonSerializer.Serialize(new Line(new DateTimeOffset(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            KindName(kind), text), LineJson);

    private static StoredEvent? Deserialize(string line)
    {
        try
        {
            var l = JsonSerializer.Deserialize<Line>(line, LineJson);
            if (l is null || KindOf(l.K) is not { } kind) return null;
            return new StoredEvent(DateTimeOffset.FromUnixTimeSeconds(l.T).UtcDateTime, kind, l.M);
        }
        catch
        {
            return null;   // a line cut short by a crash is skipped, not fatal
        }
    }

    private static string KindName(PlayerEventKind kind) => kind switch
    {
        PlayerEventKind.Join => "join",
        PlayerEventKind.Leave => "leave",
        PlayerEventKind.Chat => "chat",
        PlayerEventKind.Death => "death",
        _ => "adv"
    };

    private static PlayerEventKind? KindOf(string name) => name switch
    {
        "join" => PlayerEventKind.Join,
        "leave" => PlayerEventKind.Leave,
        "chat" => PlayerEventKind.Chat,
        "death" => PlayerEventKind.Death,
        "adv" => PlayerEventKind.Advancement,
        _ => null
    };

    private IEnumerable<string> ReadLines(string key)
    {
        var path = EventsPath(key);
        if (!File.Exists(path)) return Array.Empty<string>();
        try
        {
            return File.ReadAllLines(path, Utf8).Where(l => l.Length > 0).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private PlayerRecord GetOrAdd(string key, string name)
    {
        if (!_index.Players.TryGetValue(key, out var rec))
            _index.Players[key] = rec = new PlayerRecord();
        rec.Name = name;   // the spelling the player uses now
        return rec;
    }

    private Index LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath) &&
                JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath, Utf8), IndexJson) is { } loaded)
            {
                loaded.Players = new Dictionary<string, PlayerRecord>(loaded.Players, StringComparer.Ordinal);
                return loaded;
            }
        }
        catch
        {
            // A damaged index starts over rather than taking the Players tab down with it.
        }
        return new Index();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    /// <summary>The file and index key: the name in lower case. Names are ASCII letters, digits and _.</summary>
    private static string Key(string player) => player.ToLowerInvariant();

    // Also what keeps a name from ever becoming a path: nothing but [A-Za-z0-9_] reaches Key().
    private static bool IsName(string? player) => player is not null && PlayerName().IsMatch(player);

    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();
}
