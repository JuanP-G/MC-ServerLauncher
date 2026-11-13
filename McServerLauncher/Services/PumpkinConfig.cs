using System.IO;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// The little the app needs from <c>pumpkin.toml</c>, the one file Pumpkin is configured with.
/// </summary>
/// <remarks>
/// <para>
/// A Pumpkin server has no <c>server.properties</c>. Its port, its MOTD, its player limit and its
/// whitelist switch live in <c>pumpkin.toml</c>, under other names and inside tables
/// (<c>[networking.java] address = "0.0.0.0:25565"</c>). A dozen places in the app ask
/// <see cref="ServerPropertiesService"/> for the port alone — the busy-port check, wake on demand,
/// the tunnels, the firewall, the connection test — so the answer is given there once, in the
/// vocabulary those places already use (<see cref="ReadAsProperties"/>), instead of teaching each of
/// them a second file.
/// </para>
/// <para>
/// Read-only apart from <see cref="WriteInitial"/>. This is not a TOML library and does not try to
/// be: it reads the plain <c>key = value</c> lines of the tables it knows, which is how Pumpkin
/// itself writes the file. Rewriting a file it only partly understands would be the way to lose
/// somebody's settings, so changing them is done in the file — the app opens it.
/// </para>
/// </remarks>
public static class PumpkinConfig
{
    /// <summary>The file's name, next to the executable.</summary>
    public const string FileName = "pumpkin.toml";

    /// <summary>Pumpkin's own defaults, for a file that leaves a value out.</summary>
    internal const int DefaultPort = 25565;
    internal const string DefaultMotd = "A blazingly fast Pumpkin server!";
    internal const int DefaultMaxPlayers = 1000;

    /// <summary>The path of <c>pumpkin.toml</c> in a server folder.</summary>
    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>
    /// Whether <paramref name="folder"/> is a Pumpkin server's: it has a <c>pumpkin.toml</c> and no
    /// <c>server.properties</c>.
    /// </summary>
    /// <remarks>
    /// Asked of the folder rather than of a <c>ServerConfig</c>, because the services that need the
    /// answer are handed a path and nothing else. The second half is what keeps a Java server that
    /// somebody dropped a stray <c>pumpkin.toml</c> into from changing what it is.
    /// </remarks>
    public static bool IsServerFolder(string? folder) =>
        !string.IsNullOrEmpty(folder)
        && File.Exists(PathIn(folder))
        && !File.Exists(Path.Combine(folder, "server.properties"));

    /// <summary>
    /// Whether <paramref name="propertiesPath"/> — where a <c>server.properties</c> would be — is
    /// in a Pumpkin server's folder, so that <c>pumpkin.toml</c> answers in its place.
    /// </summary>
    public static bool StandsInFor(string propertiesPath) =>
        string.Equals(Path.GetFileName(propertiesPath), "server.properties", StringComparison.OrdinalIgnoreCase)
        && IsServerFolder(Path.GetDirectoryName(propertiesPath));

    /// <summary>
    /// What <c>pumpkin.toml</c> says, under the names <c>server.properties</c> uses for the same
    /// things: <c>server-port</c>, <c>motd</c>, <c>max-players</c>, <c>white-list</c>,
    /// <c>online-mode</c>, <c>level-name</c>.
    /// </summary>
    /// <remarks>
    /// A value the file leaves out is Pumpkin's default, not Minecraft's: an absent port is still
    /// 25565, but an absent player limit is 1000, and saying 20 would be describing another server.
    /// </remarks>
    public static Dictionary<string, string> ReadAsProperties(string folder)
    {
        var toml = Read(PathIn(folder));

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server-port"] = (PortOf(Value(toml, "networking.java", "address")) ?? DefaultPort).ToString(),
            ["motd"] = Value(toml, "networking.java", "motd") ?? DefaultMotd,
            ["max-players"] = Value(toml, "networking.java", "max_players") ?? DefaultMaxPlayers.ToString(),
            ["white-list"] = Value(toml, "", "white_list") ?? "false",
            ["online-mode"] = Value(toml, "networking.java", "online_mode") ?? "true",
            [ServerPropertiesService.LevelNameKey] = Value(toml, "", "default_level_name") ?? ServerPropertiesService.DefaultLevelName,
        };
        return result;
    }

    /// <summary>The port in an address such as <c>0.0.0.0:25565</c> or <c>[::]:25565</c>.</summary>
    internal static int? PortOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        var colon = address.LastIndexOf(':');
        return colon >= 0 && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and <= 65535
            ? port
            : null;
    }

    /// <summary>
    /// Writes the first <c>pumpkin.toml</c> of a new server: its port, its name as the MOTD, and
    /// its seed. Pumpkin fills in everything else with its defaults the first time it starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never over an existing file: a folder that already has one has an owner who wrote it.
    /// </para>
    /// <para>
    /// <c>[telemetry] enabled = false</c>: Pumpkin sends an anonymous heartbeat to its project by
    /// default. This app sends nothing anywhere unasked, and a server it sets up keeps to that; the
    /// owner can turn it on in the file, where Pumpkin documents it.
    /// </para>
    /// </remarks>
    /// <returns>False when the file was already there and was left alone.</returns>
    public static bool WriteInitial(string folder, int port, string motd, string? seed)
    {
        var path = PathIn(folder);
        if (File.Exists(path)) return false;

        var text = new StringBuilder();
        // Top-level keys first: in TOML everything after a [table] header belongs to that table.
        if (!string.IsNullOrWhiteSpace(seed))
            text.Append("seed = ").Append(Quote(seed.Trim())).Append('\n');

        text.Append('\n').Append("[networking.java]\n");
        text.Append("address = ").Append(Quote($"0.0.0.0:{port}")).Append('\n');
        text.Append("motd = ").Append(Quote(motd)).Append('\n');

        text.Append('\n').Append("[networking.bedrock]\n");
        text.Append("motd = ").Append(Quote(motd)).Append('\n');

        text.Append('\n').Append("[telemetry]\n");
        text.Append("enabled = false\n");

        AtomicTextFile.WriteIfChanged(path, text.ToString());
        return true;
    }

    /// <summary>A TOML basic string. Line breaks are dropped: one would end the value and start a key.</summary>
    internal static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': text.Append(@"\\"); break;
                case '"': text.Append("\\\""); break;
                case '\r' or '\n': break;
                case '\t': text.Append(@"\t"); break;
                default:
                    if (!char.IsControl(c)) text.Append(c);
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    private static string? Value(Dictionary<string, string> toml, string table, string key) =>
        toml.TryGetValue(table.Length == 0 ? key : $"{table}.{key}", out var value) ? value : null;

    /// <summary>Every plain <c>key = value</c> of the file, as <c>table.key</c>. Empty when unreadable.</summary>
    internal static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines;
        try
        {
            if (!File.Exists(path)) return result;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        var table = string.Empty;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '[')
            {
                // "[[array.of.tables]]" is not something the app reads; its keys are filed under a
                // name nothing asks for.
                var close = line.IndexOf(']');
                table = close > 1 ? line[1..close].Trim().Trim('[') : string.Empty;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim().Trim('"');
            var value = Unquote(line[(eq + 1)..].Trim());
            if (value is null) continue;

            result[table.Length == 0 ? key : $"{table}.{key}"] = value;
        }
        return result;
    }

    /// <summary>A string, number or boolean as text; null for anything else (arrays, inline tables).</summary>
    private static string? Unquote(string value)
    {
        if (value.Length == 0 || value[0] is '[' or '{') return null;

        if (value[0] == '\'')
        {
            var end = value.IndexOf('\'', 1);
            return end > 0 ? value[1..end] : null;
        }

        if (value[0] != '"')
        {
            // A bare value runs to a comment or the end of the line.
            var hash = value.IndexOf('#');
            return (hash >= 0 ? value[..hash] : value).Trim();
        }

        var text = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"') return text.ToString();
            if (c != '\\' || i + 1 >= value.Length) { text.Append(c); continue; }

            var next = value[++i];
            text.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => next   // \\ and \" — and anything rarer is kept as the character itself
            });
        }
        return null;   // no closing quote: not a value this reader understands
    }
}
