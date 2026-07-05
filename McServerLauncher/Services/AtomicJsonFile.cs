using System.IO;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Atomic load/save for the app's JSON files (servers.json, settings.json), so a crash or power
/// loss mid-write can never truncate them. Writes go to a ".tmp" file that atomically replaces the
/// target, keeping the previous version as ".bak". A corrupt file is quarantined as ".bad" and the
/// ".bak" copy is restored when possible, instead of silently starting from scratch.
/// </summary>
public static class AtomicJsonFile
{
    /// <summary>What happened when loading a JSON file.</summary>
    public enum LoadOutcome
    {
        /// <summary>The file was read normally (or didn't exist yet).</summary>
        Ok,

        /// <summary>The file was corrupt; the last good ".bak" copy was restored.</summary>
        RecoveredFromBackup,

        /// <summary>The file was corrupt and there was no usable ".bak"; starting from defaults.</summary>
        CorruptNoBackup
    }

    /// <summary>
    /// Serializes <paramref name="value"/> and writes it atomically: the JSON goes to
    /// "&lt;path&gt;.tmp" first and then replaces <paramref name="path"/> in a single filesystem
    /// operation (same volume), keeping the previous content as "&lt;path&gt;.bak". A crash
    /// mid-write leaves at worst a truncated .tmp — never a truncated real file.
    /// </summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, options));
        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    /// <summary>
    /// Reads and deserializes <paramref name="path"/>. If the JSON is corrupt, quarantines it as
    /// "&lt;path&gt;.bad" and tries the last good "&lt;path&gt;.bak" copy (re-writing it as the
    /// real file so the next start is normal). Returns the value (null when the file is missing or
    /// unrecoverable) plus what happened, so callers can tell the user instead of failing silently.
    /// </summary>
    public static (T? Value, LoadOutcome Outcome) Load<T>(string path, JsonSerializerOptions? options = null)
        where T : class
    {
        if (!File.Exists(path))
            return (null, LoadOutcome.Ok);

        try
        {
            return (JsonSerializer.Deserialize<T>(File.ReadAllText(path), options), LoadOutcome.Ok);
        }
        catch
        {
            // Keep the evidence: move the corrupt file aside instead of overwriting it later.
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch { /* locked/permissions: leave it in place; the .bak attempt below still applies */ }

            try
            {
                var bak = path + ".bak";
                if (File.Exists(bak))
                {
                    var recovered = JsonSerializer.Deserialize<T>(File.ReadAllText(bak), options);
                    if (recovered is not null)
                    {
                        Write(path, recovered, options); // self-heal
                        return (recovered, LoadOutcome.RecoveredFromBackup);
                    }
                }
            }
            catch { /* the backup is also unreadable: fall through */ }

            return (null, LoadOutcome.CorruptNoBackup);
        }
    }
}
