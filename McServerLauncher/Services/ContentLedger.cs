using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// What the app has to remember about a server's mods that the folder itself cannot say.
/// </summary>
/// <remarks>
/// <para>
/// Two things the jars on disk do not carry. <b>When a mod was switched off</b>: disabling renames
/// <c>x.jar</c> to <c>x.jar.disabled</c>, and a rename keeps the file's dates, so "off for two
/// months" is only knowable if somebody wrote down the day. <b>Who used to need a library</b>: once
/// the mod that needed it is gone, nothing on disk says it ever existed, and "only litematica used
/// this, and you removed it" is the explanation that makes deleting malilib a decision rather than a
/// gamble.
/// </para>
/// <para>
/// Keys are file names without <c>.disabled</c>, so the same mod is one entry whether it is on or
/// off; library keys are the library's own id, so they survive the library being updated.
/// </para>
/// </remarks>
public sealed class ContentLedger
{
    /// <summary>The file format, in case it ever has to change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>When each disabled jar was switched off, by file name without <c>.disabled</c>.</summary>
    public Dictionary<string, DateTime> DisabledSinceUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The disabled jars whose date is only when the app first saw them, not when they were turned off.
    /// </summary>
    /// <remarks>
    /// Jars already off before this existed, or turned off by hand outside the app. Their real date is
    /// earlier, which is why the panel says "at least since".
    /// </remarks>
    public HashSet<string> DisabledFirstSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>By library id, the names of the mods that last needed it.</summary>
    public Dictionary<string, List<string>> DependentsByLibrary { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the user said "not now" to a suggestion, by suggestion key.</summary>
    public Dictionary<string, DateTime> DismissedUtc { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Loads and saves one server's <see cref="ContentLedger"/>.</summary>
/// <remarks>
/// Under <c>%APPDATA%\McServerLauncher\content\&lt;serverId&gt;\ledger.json</c>, beside the player
/// history and for the same reason: it is about the app's view of the server, so it neither travels
/// with the world nor fills its backups. Losing it costs nothing worse than dates starting again.
/// </remarks>
public sealed class ContentLedgerStore
{
    /// <summary>Where every server's ledger lives. Settable so tests can point it at a temp folder.</summary>
    public static string RootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "content");

    private static readonly ConcurrentDictionary<string, ContentLedgerStore> Open = new(StringComparer.Ordinal);

    /// <summary>The store for a server, shared by everything that asks for it.</summary>
    public static ContentLedgerStore For(string serverId) =>
        Open.GetOrAdd(Path.Combine(RootDirectory, serverId), dir => new ContentLedgerStore(dir));

    private readonly string _path;
    private readonly object _lock = new();

    public ContentLedgerStore(string directory)
    {
        _path = Path.Combine(directory, "ledger.json");

        // A missing or unreadable file is an empty ledger. Nothing in it is worth refusing to open
        // the Mods tab over.
        var (loaded, _) = AtomicJsonFile.Load<ContentLedger>(_path);
        Current = Normalise(loaded ?? new ContentLedger());
    }

    /// <summary>The ledger as it is now. Changed in place, then <see cref="Save"/>d.</summary>
    public ContentLedger Current { get; }

    /// <summary>Takes the lock every change to <see cref="Current"/> has to hold.</summary>
    public object Gate => _lock;

    /// <summary>Writes the ledger to disk.</summary>
    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicJsonFile.Write(_path, Current);
        }
    }

    /// <summary>
    /// The dictionaries as the code expects them: case-insensitive, which JSON cannot record.
    /// </summary>
    private static ContentLedger Normalise(ContentLedger read) => new()
    {
        Version = read.Version,
        DisabledSinceUtc = new(read.DisabledSinceUtc ?? new(), StringComparer.OrdinalIgnoreCase),
        DisabledFirstSeen = new(read.DisabledFirstSeen ?? new(), StringComparer.OrdinalIgnoreCase),
        DependentsByLibrary = new(read.DependentsByLibrary ?? new(), StringComparer.OrdinalIgnoreCase),
        DismissedUtc = new(read.DismissedUtc ?? new(), StringComparer.Ordinal)
    };
}
