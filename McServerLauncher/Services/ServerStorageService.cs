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

    /// <summary>
    /// What happened with servers.json on the last <see cref="Load"/>. Anything other than
    /// <see cref="AtomicJsonFile.LoadOutcome.Ok"/> should be surfaced to the user (the UI does it
    /// on startup) instead of silently showing an empty server list.
    /// </summary>
    public AtomicJsonFile.LoadOutcome LastLoadOutcome { get; private set; } = AtomicJsonFile.LoadOutcome.Ok;

    /// <summary>Where a corrupt servers.json is preserved (the ".bad" quarantine file).</summary>
    public string QuarantinedFilePath => _filePath + ".bad";

    /// <summary>
    /// Loads the list of servers. Returns an empty list if there is no file. If the file is
    /// corrupt, the last good ".bak" copy is restored when possible (see <see cref="AtomicJsonFile"/>);
    /// check <see cref="LastLoadOutcome"/> for what happened.
    /// </summary>
    public List<ServerConfig> Load()
    {
        var (list, outcome) = AtomicJsonFile.Load<List<ServerConfig>>(_filePath, JsonOptions);
        LastLoadOutcome = outcome;
        return list ?? new List<ServerConfig>();
    }

    /// <summary>Saves the list of servers (atomically; the previous version is kept as ".bak").</summary>
    public void Save(IEnumerable<ServerConfig> servers)
    {
        Directory.CreateDirectory(_dataDir);
        AtomicJsonFile.Write(_filePath, servers.ToList(), JsonOptions);
    }
}
