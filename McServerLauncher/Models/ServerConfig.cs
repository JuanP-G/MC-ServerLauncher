using System.IO;
using System.Text.Json.Serialization;
using McServerLauncher.Localization;

namespace McServerLauncher.Models;

public enum ServerType
{
    Vanilla,
    Fabric,
    Forge,
    Paper
}

/// <summary>
/// Persisted data of a Minecraft server registered in the application.
/// Stored in %APPDATA%\McServerLauncher\servers.json.
/// </summary>
public class ServerConfig
{
    /// <summary>Stable identifier (so we don't depend on the name, which may change).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name of the server (e.g. "Survival", "Modded").</summary>
    public string Name { get; set; } = Localizer.Get("Name_NewServer");

    /// <summary>Server root folder (where the .jar and server.properties live).</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Server .jar file name (relative to the folder). Defaults to server.jar.</summary>
    public string JarFile { get; set; } = "server.jar";

    /// <summary>Type of the server (Vanilla, Fabric, Forge).</summary>
    public ServerType Type { get; set; } = ServerType.Vanilla;

    /// <summary>Minecraft game version (e.g. 1.20.1).</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>Version of the mod loader (e.g. 0.16.2 for Fabric).</summary>
    public string ModLoaderVersion { get; set; } = string.Empty;

    /// <summary>
    /// For modern Forge (1.17+) the server has no runnable jar; it is launched via an args file
    /// under <c>libraries/net/minecraftforge/forge/&lt;id&gt;/{win,unix}_args.txt</c>. When this holds
    /// that Forge id (e.g. "1.20.1-47.2.0") the launcher uses the args file instead of "-jar".
    /// Empty means the classic "-jar JarFile" launch (Vanilla, Fabric, old Forge ≤1.16.5).
    /// </summary>
    public string ForgeArgs { get; set; } = string.Empty;

    /// <summary>Path to the Java executable. "java" uses the one on the PATH.</summary>
    public string JavaPath { get; set; } = "java";

    /// <summary>Minimum memory in GB (-Xms). Same default the create dialog suggests.</summary>
    public int MinRamGb { get; set; } = 2;

    /// <summary>Maximum memory in GB (-Xmx). Same default the create dialog suggests.</summary>
    public int MaxRamGb { get; set; } = 4;

    /// <summary>Extra JVM arguments (optional, e.g. GC flags).</summary>
    public string ExtraJvmArgs { get; set; } = string.Empty;

    // --- Playit.gg ---

    /// <summary>Whether the Playit.gg integration is enabled for this server.</summary>
    public bool PlayitEnabled { get; set; }

    /// <summary>
    /// Public tunnel address for this server. It is detected automatically when running
    /// playit, but it can also be typed/pasted by hand and is kept saved.
    /// </summary>
    public string? TunnelAddress { get; set; }

    // --- World backups ---

    /// <summary>Whether a zip backup of the world is made before starting and after an explicit stop.</summary>
    public bool BackupsEnabled { get; set; } = true;

    /// <summary>How many backups to keep; older ones are deleted after each new one.</summary>
    public int BackupRetention { get; set; } = 5;

    /// <summary>Full path to the .jar combining folder + jar name.</summary>
    [JsonIgnore]
    public string JarFullPath => Path.Combine(FolderPath, JarFile);

    /// <summary>Path to server.properties inside the server folder.</summary>
    [JsonIgnore]
    public string PropertiesPath => Path.Combine(FolderPath, "server.properties");
}
