namespace McServerLauncher.Services;

/// <summary>
/// The <c>server.properties</c> values saved while the server was running, to be written again when
/// the server overwrites them.
/// </summary>
/// <remarks>
/// <para>
/// A running Minecraft server keeps the properties it loaded at start in memory, and whenever its
/// whitelist is switched (<c>whitelist on</c>/<c>off</c>) it writes all of them back to the file.
/// Whatever the settings page had saved since — a new MOTD, more players — was silently put back the
/// way it was, the moment somebody used the switch on the Players tab.
/// </para>
/// <para>
/// So the app remembers what it saved while the server ran, and writes it again after each of those
/// commands. <c>white-list</c> itself is never remembered: it is what the command just set, and
/// putting back an older value would undo the switch. Forgotten when the server stops, since the next
/// start reads the file as it is.
/// </para>
/// </remarks>
public sealed class PropertiesSavedWhileRunning
{
    private const string Whitelist = "white-list";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers <paramref name="changes"/>, saved just now with the server running.</summary>
    public void Note(IReadOnlyDictionary<string, string> changes)
    {
        foreach (var (key, value) in changes)
            if (!string.Equals(key, Whitelist, StringComparison.OrdinalIgnoreCase)) _values[key] = value;
    }

    /// <summary>What to write again after the server rewrote the file; empty when nothing is owed.</summary>
    public Dictionary<string, string> ToRestore() => new(_values, StringComparer.OrdinalIgnoreCase);

    /// <summary>The server stopped: the next start reads the file as it is.</summary>
    public void Clear() => _values.Clear();
}
