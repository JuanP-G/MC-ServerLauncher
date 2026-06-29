using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McServerLauncher.Services;

/// <summary>
/// Reads the player lists the server stores in JSON files:
/// ops.json (operators), banned-players.json (banned) and usercache.json (known).
/// </summary>
public class PlayersService
{
    public List<string> ReadOps(string folder) => ReadNames(Path.Combine(folder, "ops.json"));
    public List<string> ReadBanned(string folder) => ReadNames(Path.Combine(folder, "banned-players.json"));
    public List<string> ReadKnown(string folder) => ReadNames(Path.Combine(folder, "usercache.json"));

    private static List<string> ReadNames(string path)
    {
        var list = new List<string>();
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    list.Add(name);
            }
        }
        catch
        {
            // corrupt or in-use file: return whatever we have
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    }

    /// <summary>Removes a player (by name) from banned-players.json. Returns true if any was removed.</summary>
    public bool Unban(string folder, string name)
    {
        var path = Path.Combine(folder, "banned-players.json");
        if (!File.Exists(path)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array) return false;

            var toRemove = array
                .Where(x => string.Equals(x?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var node in toRemove)
                array.Remove(node);

            if (toRemove.Count > 0)
                File.WriteAllText(path, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));

            return toRemove.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
