using System.IO;
using System.Text.Json;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Carga y guarda la lista de servidores registrados en
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

    /// <summary>Carga la lista de servidores. Devuelve lista vacía si no hay archivo.</summary>
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
            // Si el archivo está corrupto, empezamos limpio en vez de reventar la app.
            return new List<ServerConfig>();
        }
    }

    /// <summary>Guarda la lista de servidores.</summary>
    public void Save(IEnumerable<ServerConfig> servers)
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(servers.ToList(), JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
