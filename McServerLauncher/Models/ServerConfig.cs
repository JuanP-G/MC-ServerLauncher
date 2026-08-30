using System.IO;
using System.Text.Json.Serialization;
using McServerLauncher.Localization;

namespace McServerLauncher.Models;

/// <summary>What kind of server this is: which loader, if any, runs the mods or plugins.</summary>
/// <remarks>
/// <para>
/// <strong>The numbers are part of the file format.</strong> servers.json is written with the
/// default serializer options, so these persist as integers, not names — a real config reads
/// <c>"Type": 0</c>. Inserting a member anywhere but the end silently renumbers the ones after it,
/// and every existing Forge server on every machine would come back as something else the next
/// time the app opened. Add to the bottom, never in the middle, and never reorder.
/// </para>
/// <para>
/// The values are written out explicitly so that rule is visible at the point where it could be
/// broken, rather than implied.
/// </para>
/// </remarks>
public enum ServerType
{
    Vanilla = 0,
    Fabric = 1,
    Forge = 2,
    Paper = 3,
    NeoForge = 4,
    // Purpur is a Paper fork: same plugins, more configuration. Appended, like every type after
    // it must be — these numbers are the file format, not a display order.
    Purpur = 5
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

    /// <summary>Type of the server (Vanilla, Fabric, Forge, Paper, NeoForge).</summary>
    public ServerType Type { get; set; } = ServerType.Vanilla;

    /// <summary>Minecraft game version (e.g. 1.20.1).</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>Version of the mod loader (e.g. 0.16.2 for Fabric).</summary>
    public string ModLoaderVersion { get; set; } = string.Empty;

    /// <summary>
    /// Modern Forge (1.17+) and every NeoForge build have no runnable jar; they are launched via an
    /// args file under <c>libraries/&lt;loader-root&gt;/&lt;id&gt;/{win,unix}_args.txt</c>. When this
    /// holds that id — "1.20.1-47.2.0" for Forge, "21.1.248" for NeoForge — the launcher uses the
    /// args file instead of "-jar". Empty means the classic "-jar JarFile" launch (Vanilla, Fabric,
    /// old Forge ≤1.16.5).
    /// <para>
    /// The name stays <c>ForgeArgs</c> even though it now covers both: it is the key in every
    /// existing servers.json, and renaming it would leave installed Forge servers unable to start.
    /// </para>
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

    // --- Notifications ---

    /// <summary>
    /// When true, this server uses its own <see cref="Notifications"/> instead of the global
    /// notification settings. When false (default), the global settings apply.
    /// </summary>
    /// <summary>
    /// Minutes with nobody connected before the server stops itself. <c>0</c> means never.
    /// </summary>
    /// <remarks>
    /// Zero is the default so no existing server changes behaviour on update: a server that used to
    /// stay up forever keeps doing exactly that until its owner asks for something else.
    /// </remarks>
    public int IdleShutdownMinutes { get; set; }

    /// <summary>
    /// While stopped, answer on the server's port so that someone trying to join starts it.
    /// </summary>
    /// <remarks>
    /// Off by default: it opens a listening socket, and nobody should end up with one without
    /// having asked. Pairs with <see cref="IdleShutdownMinutes"/> — sleep when empty, wake on
    /// demand — but each half works on its own.
    /// </remarks>
    public bool WakeOnDemand { get; set; }

    // --- Crossplay (Java + Bedrock) ---

    /// <summary>
    /// Whether Bedrock players (phone, console, Windows 10/11) can join this server too.
    /// </summary>
    /// <remarks>
    /// Remembered rather than set up once and forgotten, because it is not a one-off action: it
    /// needs a second tunnel to keep existing, and Geyser has to keep advertising that tunnel's
    /// public port. Both can drift, and only something that knows the server is meant to be
    /// crossplay can put them back.
    /// </remarks>
    public bool CrossplayEnabled { get; set; }

    /// <summary>Whether ViaVersion and ViaBackwards are installed, for joining from other versions.</summary>
    /// <remarks>
    /// Separate from <see cref="CrossplayEnabled"/> on purpose. Geyser does not need these to work,
    /// and a Java-only server benefits from them just as much: they are about which Minecraft
    /// <em>versions</em> may connect, not which edition.
    /// </remarks>
    public bool MultiVersionEnabled { get; set; }

    /// <summary>The local <em>UDP</em> port Geyser listens on. 0 until crossplay is set up.</summary>
    /// <remarks>
    /// UDP, and a different namespace from the Java port: this one can be 19132 while some other
    /// program holds TCP 19132, and vice versa.
    /// </remarks>
    public int BedrockPort { get; set; }

    public bool UseCustomNotifications { get; set; }

    /// <summary>Per-server notification override, used only when <see cref="UseCustomNotifications"/>.</summary>
    public NotificationSettings? Notifications { get; set; }

    /// <summary>Full path to the .jar combining folder + jar name.</summary>
    [JsonIgnore]
    public string JarFullPath => Path.Combine(FolderPath, JarFile);

    /// <summary>Path to server.properties inside the server folder.</summary>
    [JsonIgnore]
    public string PropertiesPath => Path.Combine(FolderPath, "server.properties");
}
