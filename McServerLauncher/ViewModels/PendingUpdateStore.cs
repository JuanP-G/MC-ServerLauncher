using McServerLauncher.Models;

namespace McServerLauncher.ViewModels;

/// <summary>
/// The newer versions the last update check found, kept apart from the rows that show them.
/// </summary>
/// <remarks>
/// <para>
/// They used to live only on each <see cref="ModItem"/>, and the list is rebuilt from disk after
/// every update, every enable or disable, every delete. So updating one mod out of five threw
/// away the other four, and the only way to get their buttons back was to check again.
/// </para>
/// <para>
/// Keyed by the jar's enabled path, so disabling a mod — which renames it to
/// <c>.jar.disabled</c> — keeps its update. Stamped with the size and write time the check saw,
/// so a jar replaced by hand is not offered an update found for the file it used to be.
/// </para>
/// <para>
/// A check answers "newest compatible with this server as it is now". Convert the server or move
/// it to another Minecraft version and every answer is for something else, so they are dropped
/// rather than offered — installing them is how a server ends up with mods it cannot load.
/// </para>
/// </remarks>
internal sealed class PendingUpdateStore
{
    /// <summary>One newer version, and the file it was found for.</summary>
    private sealed record Pending(ModUpdateInfo Info, long Length, DateTime WrittenUtc);

    private readonly ServerConfig _config;
    private readonly Dictionary<string, Pending> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The loader and Minecraft version the pending updates were found for.</summary>
    private (ServerType Type, string GameVersion) _foundFor;

    public PendingUpdateStore(ServerConfig config) => _config = config;

    /// <summary>The key a jar's pending update is kept under: its path with any .disabled removed.</summary>
    public static string KeyFor(string filePath) =>
        filePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? filePath[..^".disabled".Length]
            : filePath;

    /// <summary>Starts a fresh set, for the server as it is now. A new check replaces the last one's.</summary>
    public void Begin()
    {
        _byPath.Clear();
        _foundFor = (_config.Type, _config.GameVersion);
    }

    /// <summary>Remembers a newer version for a jar, as the check saw it.</summary>
    public void Remember(string filePath, ModUpdateInfo info)
    {
        var file = new FileInfo(filePath);
        _byPath[KeyFor(filePath)] = new Pending(info, file.Length, file.LastWriteTimeUtc);
    }

    /// <summary>Drops the update for a jar, because it was just applied.</summary>
    public void Forget(string filePath) => _byPath.Remove(KeyFor(filePath));

    /// <summary>
    /// The update still waiting for this jar, or null. Drops what no longer applies on the way.
    /// </summary>
    public ModUpdateInfo? For(string filePath)
    {
        if (_foundFor != (_config.Type, _config.GameVersion))
        {
            _byPath.Clear();
            return null;
        }

        var key = KeyFor(filePath);
        if (!_byPath.TryGetValue(key, out var pending)) return null;

        var file = new FileInfo(filePath);
        if (!file.Exists || file.Length != pending.Length || file.LastWriteTimeUtc != pending.WrittenUtc)
        {
            // Not the file the check looked at any more: replaced by hand, or already updated.
            _byPath.Remove(key);
            return null;
        }

        return pending.Info;
    }
}
