using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// <remarks>
/// <para>
/// <strong>It announces its own changes</strong>, which is the whole reason it is not a plain
/// object. The view models and the edit dialog share one instance: the dialog writes into the very
/// config the cards are showing. While this was a POCO, nothing derived from it ever recomputed, so
/// converting a server or moving it to another Minecraft version changed the disk and left the app
/// describing what the folder used to be until it was restarted. Every attempted fix was a method
/// somebody had to remember to call.
/// </para>
/// <para>
/// Being observable does not change what is written: reflection-based System.Text.Json serializes
/// public instance <em>properties</em>, the generated properties keep the exact names the old
/// auto-properties had, and <see cref="ObservableObject"/> contributes only events. The one thing
/// that is not promised is the <em>order</em> of the keys in the file, which nothing reads
/// positionally. See <c>ServerConfigFormatTests</c>, which holds all of that down.
/// </para>
/// <para>
/// Never give this class a <c>Clone</c> built on <c>MemberwiseClone</c>: it would copy the
/// <c>PropertyChanged</c> delegate too, and the copy would raise changes at the original's
/// subscribers. <see cref="NotificationSettings.Clone"/> is field-by-field for the same reason.
/// </para>
/// </remarks>
public partial class ServerConfig : ObservableObject
{
    /// <summary>Stable identifier (so we don't depend on the name, which may change).</summary>
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    /// <summary>Display name of the server (e.g. "Survival", "Modded").</summary>
    [ObservableProperty]
    private string _name = Localizer.Get("Name_NewServer");

    /// <summary>Server root folder (where the .jar and server.properties live).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JarFullPath))]
    [NotifyPropertyChangedFor(nameof(PropertiesPath))]
    private string _folderPath = string.Empty;

    /// <summary>Server .jar file name (relative to the folder). Defaults to server.jar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JarFullPath))]
    private string _jarFile = "server.jar";

    /// <summary>Type of the server (Vanilla, Fabric, Forge, Paper, NeoForge).</summary>
    [ObservableProperty]
    private ServerType _type = ServerType.Vanilla;

    /// <summary>Minecraft game version (e.g. 1.20.1).</summary>
    [ObservableProperty]
    private string _gameVersion = string.Empty;

    /// <summary>Version of the mod loader (e.g. 0.16.2 for Fabric).</summary>
    [ObservableProperty]
    private string _modLoaderVersion = string.Empty;

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
    [ObservableProperty]
    private string _forgeArgs = string.Empty;

    /// <summary>Path to the Java executable. "java" uses the one on the PATH.</summary>
    [ObservableProperty]
    private string _javaPath = "java";

    /// <summary>Minimum memory in GB (-Xms). Same default the create dialog suggests.</summary>
    [ObservableProperty]
    private int _minRamGb = 2;

    /// <summary>Maximum memory in GB (-Xmx). Same default the create dialog suggests.</summary>
    [ObservableProperty]
    private int _maxRamGb = 4;

    /// <summary>Extra JVM arguments (optional, e.g. GC flags).</summary>
    [ObservableProperty]
    private string _extraJvmArgs = string.Empty;

    // --- Playit.gg ---

    /// <summary>Whether the Playit.gg integration is enabled for this server.</summary>
    [ObservableProperty]
    private bool _playitEnabled;

    /// <summary>
    /// Public tunnel address for this server. It is detected automatically when running
    /// playit, but it can also be typed/pasted by hand and is kept saved.
    /// </summary>
    [ObservableProperty]
    private string? _tunnelAddress;

    // --- World backups ---

    /// <summary>Whether a zip backup of the world is made before starting and after an explicit stop.</summary>
    [ObservableProperty]
    private bool _backupsEnabled = true;

    /// <summary>
    /// How many automatic backups to keep — the ones made on starting, on stopping, and by the
    /// clock. Older ones are deleted after each new one.
    /// </summary>
    [ObservableProperty]
    private int _backupRetention = 5;

    /// <summary>Whether backups are also made at intervals while the server is running.</summary>
    [ObservableProperty]
    private bool _autoBackupEnabled = true;

    /// <summary>
    /// Minutes between automatic backups while the server is running. Clamped when it is read
    /// (see <c>BackupSchedule</c>), so a hand-edited zero cannot mean "on every tick".
    /// </summary>
    [ObservableProperty]
    private int _backupIntervalMinutes = 60;

    /// <summary>
    /// How many of the backups the user asked for to keep, counted separately.
    /// </summary>
    /// <remarks>
    /// With a backup every hour, a single shared count would be full of automatic ones by the
    /// morning, and the copy somebody took by hand before trying something would be gone — which is
    /// the one backup they were sure they still had.
    /// </remarks>
    [ObservableProperty]
    private int _manualBackupRetention = 5;

    // --- Sleeping and waking ---

    /// <summary>
    /// Minutes with nobody connected before the server stops itself. <c>0</c> means never.
    /// </summary>
    /// <remarks>
    /// Zero is the default so no existing server changes behaviour on update: a server that used to
    /// stay up forever keeps doing exactly that until its owner asks for something else.
    /// </remarks>
    [ObservableProperty]
    private int _idleShutdownMinutes;

    /// <summary>
    /// While stopped, answer on the server's port so that someone trying to join starts it.
    /// </summary>
    /// <remarks>
    /// Off by default: it opens a listening socket, and nobody should end up with one without
    /// having asked. Pairs with <see cref="IdleShutdownMinutes"/> — sleep when empty, wake on
    /// demand — but each half works on its own.
    /// </remarks>
    [ObservableProperty]
    private bool _wakeOnDemand;

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
    [ObservableProperty]
    private bool _crossplayEnabled;

    /// <summary>Whether Hydraulic is installed, so Bedrock players see what the mods add.</summary>
    /// <remarks>
    /// Separate from <see cref="CrossplayEnabled"/> because it answers a different question. Geyser
    /// gets Bedrock players <em>in</em>; without this they arrive to a world whose modded blocks and
    /// items they cannot see. Fabric only — see <see cref="Services.HydraulicService"/>.
    /// </remarks>
    [ObservableProperty]
    private bool _bedrockModContentEnabled;

    /// <summary>Whether ViaVersion and ViaBackwards are installed, for joining from other versions.</summary>
    /// <remarks>
    /// Separate from <see cref="CrossplayEnabled"/> on purpose. Geyser does not need these to work,
    /// and a Java-only server benefits from them just as much: they are about which Minecraft
    /// <em>versions</em> may connect, not which edition.
    /// </remarks>
    [ObservableProperty]
    private bool _multiVersionEnabled;

    /// <summary>The local <em>UDP</em> port Geyser listens on. 0 until crossplay is set up.</summary>
    /// <remarks>
    /// UDP, and a different namespace from the Java port: this one can be 19132 while some other
    /// program holds TCP 19132, and vice versa.
    /// </remarks>
    [ObservableProperty]
    private int _bedrockPort;

    // --- Notifications ---

    /// <summary>
    /// When true, this server uses its own <see cref="Notifications"/> instead of the global
    /// notification settings. When false (default), the global settings apply.
    /// </summary>
    [ObservableProperty]
    private bool _useCustomNotifications;

    /// <summary>Per-server notification override, used only when <see cref="UseCustomNotifications"/>.</summary>
    [ObservableProperty]
    private NotificationSettings? _notifications;

    // --- World ---

    /// <summary>
    /// The seed the server last printed when asked with <c>seed</c>. Null until it has been asked.
    /// </summary>
    /// <remarks>
    /// A fallback, not the source: the seed is read from the world's own files. It is kept because
    /// Minecraft has already moved it once (26.1 took it out of level.dat), and the answer the
    /// server gives to <c>seed</c> is the one thing that cannot go stale that way.
    /// </remarks>
    [ObservableProperty]
    private long? _lastKnownSeed;

    /// <summary>Full path to the .jar combining folder + jar name.</summary>
    [JsonIgnore]
    public string JarFullPath => Path.Combine(FolderPath, JarFile);

    /// <summary>Path to server.properties inside the server folder.</summary>
    [JsonIgnore]
    public string PropertiesPath => Path.Combine(FolderPath, "server.properties");
}
