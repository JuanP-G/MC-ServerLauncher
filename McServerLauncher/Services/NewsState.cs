using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// What Novedades remembers between runs: which entries have been read, which notices the user closed
/// the strip for, and which have already been announced with a notification.
/// </summary>
/// <remarks>
/// <para>
/// A file of its own (<c>news-state.json</c>) and not a field of <see cref="Models.AppSettings"/>:
/// the settings are a document the screen edits and <c>ShallowCopy</c> hands out, and a list that
/// grows as the app is used has no business riding along in it.
/// </para>
/// <para>
/// An entry is read by its <em>key</em>: <c>update:1.23.0</c> for an offered version and
/// <c>id:status</c> for a notice. The status is part of the key so an incident that is marked
/// resolved counts as news again — to read and to be notified of. A closed strip is remembered by the
/// notice's <em>id</em> alone: closing it is saying "I know about this incident", and the same incident
/// turning resolved does not bring a strip back, because the strip is only for what is still going on.
/// </para>
/// <para>
/// Nothing here may take the screen down: a file that cannot be read starts empty (everything shows
/// as unread, which is the harmless way to be wrong, and nothing is announced twice only because the
/// notices have not been fetched yet) and one that cannot be written is not kept.
/// </para>
/// </remarks>
public sealed class NewsState
{
    /// <summary>The most read keys kept. Old ones are forgotten first; a key forgotten is merely unread again.</summary>
    internal const int MaxKeys = 200;

    /// <summary>The most closed strips and announced notices kept, forgotten oldest first.</summary>
    internal const int MaxClosed = 100;

    /// <summary>The file as stored.</summary>
    public sealed class Data
    {
        public List<string> Read { get; set; } = new();
        public List<string> Closed { get; set; } = new();
        public List<string> Toasted { get; set; } = new();
    }

    /// <summary>An ordered set that forgets its oldest entry once it holds more than its limit.</summary>
    private sealed class Keys
    {
        private readonly List<string> _order;
        private readonly HashSet<string> _set;
        private readonly int _max;

        public Keys(IEnumerable<string>? stored, int max)
        {
            _max = max;
            _order = (stored ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal)
                .TakeLast(max).ToList();
            _set = new HashSet<string>(_order, StringComparer.Ordinal);
        }

        public bool Contains(string key) => _set.Contains(key);

        /// <summary>Adds <paramref name="key"/>; false when it was there already or is empty.</summary>
        public bool Add(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !_set.Add(key)) return false;
            _order.Add(key);
            while (_order.Count > _max)
            {
                _set.Remove(_order[0]);
                _order.RemoveAt(0);
            }
            return true;
        }

        public List<string> ToList() => _order.ToList();
    }

    private readonly string _path;
    private readonly Keys _read;
    private readonly Keys _closed;
    private readonly Keys _toasted;

    /// <param name="path">The file; null for <c>news-state.json</c> in the app's own folder. Tests
    /// pass a temporary one.</param>
    public NewsState(string? path = null)
    {
        _path = path ?? AppPaths.In("news-state.json");

        Data? loaded = null;
        try { (loaded, _) = AtomicJsonFile.Load<Data>(_path); }
        catch { /* unreadable: start empty */ }

        _read = new Keys(loaded?.Read, MaxKeys);
        _closed = new Keys(loaded?.Closed, MaxClosed);
        _toasted = new Keys(loaded?.Toasted, MaxKeys);
    }

    public bool IsRead(string key) => _read.Contains(key);

    /// <summary>Marks <paramref name="keys"/> read and writes the file; false when there was nothing new.</summary>
    public bool MarkRead(IEnumerable<string> keys)
    {
        var changed = false;
        foreach (var key in keys) changed |= _read.Add(key);
        if (changed) Save();
        return changed;
    }

    /// <summary>Whether the user closed the strip of the notice with this id.</summary>
    public bool IsClosed(string id) => _closed.Contains(id);

    /// <summary>Remembers that the strip of the notice with this id was closed; false when it already was.</summary>
    public bool Close(string id)
    {
        if (!_closed.Add(id)) return false;
        Save();
        return true;
    }

    /// <summary>Whether the notice under this key (<see cref="NewsBoard.NoticeKey"/>) has been announced already.</summary>
    public bool WasToasted(string key) => _toasted.Contains(key);

    /// <summary>Remembers that the notice under this key was announced (or deliberately not), so it never is twice.</summary>
    public bool MarkToasted(string key)
    {
        if (!_toasted.Add(key)) return false;
        Save();
        return true;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicJsonFile.Write(_path, new Data
            {
                Read = _read.ToList(),
                Closed = _closed.ToList(),
                Toasted = _toasted.ToList(),
            });
        }
        catch { /* not kept; the entries show as unread next time */ }
    }
}
