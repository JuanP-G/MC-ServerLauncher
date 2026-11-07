using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// What Novedades remembers between runs: which entries have been read.
/// </summary>
/// <remarks>
/// <para>
/// A file of its own (<c>news-state.json</c>) and not a field of <see cref="Models.AppSettings"/>:
/// the settings are a document the screen edits and <c>ShallowCopy</c> hands out, and a list that
/// grows as the app is used has no business riding along in it.
/// </para>
/// <para>
/// An entry is read by its <em>key</em>: <c>update:1.23.0</c> for an offered version and, later,
/// <c>id:status</c> for a notice. The status is part of the key so an incident that is marked
/// resolved counts as news again.
/// </para>
/// <para>
/// Nothing here may take the screen down: a file that cannot be read starts empty (everything shows
/// as unread, which is the harmless way to be wrong) and one that cannot be written is not kept.
/// </para>
/// </remarks>
public sealed class NewsState
{
    /// <summary>The most keys kept. Old ones are forgotten first; a key forgotten is merely unread again.</summary>
    internal const int MaxKeys = 200;

    /// <summary>The file as stored.</summary>
    public sealed class Data
    {
        public List<string> Read { get; set; } = new();
    }

    private readonly string _path;
    private readonly List<string> _order;
    private readonly HashSet<string> _read;

    /// <param name="path">The file; null for <c>news-state.json</c> in the app's own folder. Tests
    /// pass a temporary one.</param>
    public NewsState(string? path = null)
    {
        _path = path ?? AppPaths.In("news-state.json");

        List<string> stored = new();
        try
        {
            var (loaded, _) = AtomicJsonFile.Load<Data>(_path);
            stored = loaded?.Read ?? new();
        }
        catch { /* unreadable: start empty */ }

        _order = stored.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal)
            .TakeLast(MaxKeys).ToList();
        _read = new HashSet<string>(_order, StringComparer.Ordinal);
    }

    public bool IsRead(string key) => _read.Contains(key);

    /// <summary>Marks <paramref name="keys"/> read and writes the file; false when there was nothing new.</summary>
    public bool MarkRead(IEnumerable<string> keys)
    {
        var changed = false;
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key) || !_read.Add(key)) continue;
            _order.Add(key);
            changed = true;
        }
        if (!changed) return false;

        while (_order.Count > MaxKeys)
        {
            _read.Remove(_order[0]);
            _order.RemoveAt(0);
        }
        Save();
        return true;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicJsonFile.Write(_path, new Data { Read = _order.ToList() });
        }
        catch { /* not kept; the entries show as unread next time */ }
    }
}
