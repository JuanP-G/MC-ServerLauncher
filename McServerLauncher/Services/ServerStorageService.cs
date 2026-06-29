using System.IO;
using System.Text.Json;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Loads and saves the list of registered servers in
/// %APPDATA%\McServerLauncher\servers.json.
/// </summary>
public class ServerStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _dataDir;
    private readonly string _filePath;

    public ServerStorageService()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McServerLauncher");
        _filePath = Path.Combine(_dataDir, "servers.json");
    }

    /// <summary>Loads the list of servers. Returns an empty list if there is no file.</summary>
    public List<ServerConfig> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<ServerConfig>();

            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<ServerConfig>>(json, JsonOptions);
            return list ?? new List<ServerConfig>();
        }
        catch
        {
            // If the file is corrupt, start clean instead of crashing the app.
            return new List<ServerConfig>();
        }
    }

    /// <summary>Saves the list of servers.</summary>
    public void Save(IEnumerable<ServerConfig> servers)
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(servers.ToList(), JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
