using System.IO;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Reads and writes the server.properties file (key=value format, one per line).
/// Used to read values (e.g. the port) and to update them from the visual configuration screen.
/// </summary>
public class ServerPropertiesService
{
    /// <summary>
    /// Reads server.properties and returns a key→value dictionary.
    /// Returns empty if the file does not exist.
    /// </summary>
    public Dictionary<string, string> Read(string propertiesPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(propertiesPath))
            return result;

        foreach (var raw in File.ReadAllLines(propertiesPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }

    /// <summary>Returns the server port (server-port) or null if not found.</summary>
    public int? GetServerPort(string propertiesPath)
    {
        var props = Read(propertiesPath);
        if (props.TryGetValue("server-port", out var value) && int.TryParse(value, out var port))
            return port;
        return null;
    }

    /// <summary>
    /// Updates the given keys in server.properties, preserving the rest of the lines,
    /// comments and order. New keys are appended at the end. Creates the file if it doesn't exist.
    /// </summary>
    public void Update(string propertiesPath, IDictionary<string, string> changes)
    {
        var lines = File.Exists(propertiesPath)
            ? File.ReadAllLines(propertiesPath).ToList()
            : new List<string>();

        var remaining = new Dictionary<string, string>(changes, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var idx = lines[i].IndexOf('=');
            if (idx <= 0)
                continue;

            var key = lines[i][..idx].Trim();
            if (remaining.TryGetValue(key, out var value))
            {
                lines[i] = $"{key}={value}";
                remaining.Remove(key);
            }
        }

        foreach (var kv in remaining)
            lines.Add($"{kv.Key}={kv.Value}");

        File.WriteAllLines(propertiesPath, lines, new UTF8Encoding(false));
    }
}
