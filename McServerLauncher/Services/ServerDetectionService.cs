using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>What a server folder turned out to contain.</summary>
/// <remarks>
/// Every field is optional: a folder can be half a server, or not one at all, and the caller shows
/// what was found and asks for the rest rather than refusing.
/// </remarks>
public sealed record ServerDetection
{
    public ServerType? Type { get; init; }
    public string? GameVersion { get; init; }
    public string? LoaderVersion { get; init; }

    /// <summary>The jar to run, relative to the folder; null for loaders launched through an args file.</summary>
    public string? JarFile { get; init; }

    /// <summary>The Forge/NeoForge id whose args file launches the server.</summary>
    public string? ForgeArgs { get; init; }

    public int? Port { get; init; }
    public int? MinRamGb { get; init; }
    public int? MaxRamGb { get; init; }
    public bool HasWorld { get; init; }

    /// <summary>Every jar in the folder's root, installers left out, for choosing one by hand.</summary>
    public IReadOnlyList<string> Jars { get; init; } = Array.Empty<string>();

    /// <summary>Enough to start it: what it is, which Minecraft, and what to launch.</summary>
    public bool IsComplete =>
        Type is not null && !string.IsNullOrEmpty(GameVersion)
        && (!string.IsNullOrEmpty(ForgeArgs) || !string.IsNullOrEmpty(JarFile));

    public static ServerDetection Nothing { get; } = new();
}

/// <summary>
/// Works out what kind of server a folder holds: the type, the Minecraft version, the loader, what
/// to launch, and the port, memory and world it was set up with.
/// </summary>
/// <remarks>
/// <para>
/// Two callers. <see cref="Detect"/> answers without touching anything, for the create dialog's
/// "use a folder that already exists", which shows what it found before anything is saved.
/// <see cref="DetectAndFill"/> fills in servers saved before the type and version were recorded,
/// every time the app starts.
/// </para>
/// <para>
/// The order matters. NeoForge and Forge leave an args file under <c>libraries/</c> and may have a
/// stray jar in the root too, so they go first; Purpur before Paper, because a Purpur jar is not
/// called paper-anything and used to fall all the way through to "Vanilla, version unknown" — even
/// for the servers this app creates itself.
/// </para>
/// </remarks>
public partial class ServerDetectionService
{
    private readonly JavaService _java = new();

    /// <summary>Detects and fills missing fields in place. Returns true if it changed the config.</summary>
    public bool DetectAndFill(ServerConfig config)
    {
        if (!string.IsNullOrEmpty(config.GameVersion)) return false; // already known (new-style config)
        if (string.IsNullOrEmpty(config.FolderPath) || !Directory.Exists(config.FolderPath)) return false;

        var found = Detect(config.FolderPath, config.JarFile);
        if (found.Type is not { } type) return false;

        config.Type = type;
        config.GameVersion = found.GameVersion ?? string.Empty;
        if (!string.IsNullOrEmpty(found.LoaderVersion)) config.ModLoaderVersion = found.LoaderVersion;
        if (!string.IsNullOrEmpty(found.ForgeArgs)) config.ForgeArgs = found.ForgeArgs;
        if (!string.IsNullOrEmpty(found.JarFile)) config.JarFile = found.JarFile;
        return true;
    }

    /// <summary>
    /// Everything that can be told about <paramref name="folder"/>, without changing anything.
    /// </summary>
    /// <param name="folder">The server's folder.</param>
    /// <param name="preferredJar">
    /// A jar the caller already believes is the server, tried first for a plain vanilla folder.
    /// </param>
    public ServerDetection Detect(string folder, string? preferredJar = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return ServerDetection.Nothing;

        var runtime = TryNeoForge(folder) ?? TryForge(folder) ?? TryFabric(folder)
                      ?? TryPurpur(folder) ?? TryPaper(folder) ?? TryVanilla(folder, preferredJar)
                      ?? ServerDetection.Nothing;

        var (min, max) = ReadMemory(folder);
        return runtime with
        {
            Port = new ServerPropertiesService().GetServerPort(Path.Combine(folder, "server.properties")),
            MinRamGb = min,
            MaxRamGb = max,
            HasWorld = ServerCreationService.WorldExists(folder),
            Jars = RootJars(folder)
        };
    }

    // --- What it is ---

    /// <summary>
    /// A NeoForge server: an args file under libraries/net/neoforged/neoforge/&lt;version&gt;/.
    /// </summary>
    /// <remarks>
    /// Unlike Forge's, the directory name is the NeoForge build alone (e.g. "21.1.248") and already
    /// encodes the Minecraft version, so the game version is derived from it rather than split off
    /// a composite id.
    /// </remarks>
    private static ServerDetection? TryNeoForge(string folder)
    {
        var root = LoaderPaths.LibrariesRoot(folder, ServerType.NeoForge);
        if (root is null || !Directory.Exists(root)) return null;

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!HasArgsFile(dir)) continue;
            var version = Path.GetFileName(dir);
            return new ServerDetection
            {
                Type = ServerType.NeoForge,
                ForgeArgs = version,
                LoaderVersion = version,
                GameVersion = NeoForgeVersions.MinecraftVersionOf(version)
            };
        }
        return null;
    }

    private static ServerDetection? TryForge(string folder)
    {
        // Modern Forge (1.17+): an args file under libraries/net/minecraftforge/forge/<id>/.
        var forgeRoot = Path.Combine(folder, "libraries", "net", "minecraftforge", "forge");
        if (Directory.Exists(forgeRoot))
        {
            foreach (var dir in Directory.GetDirectories(forgeRoot))
            {
                if (!HasArgsFile(dir)) continue;
                var id = Path.GetFileName(dir);
                var (game, loader) = SplitForgeId(id);
                return new ServerDetection
                {
                    Type = ServerType.Forge, ForgeArgs = id, GameVersion = game, LoaderVersion = loader
                };
            }
        }

        // Old Forge (≤1.16.5): a runnable forge-*.jar in the root.
        var oldForge = Directory.EnumerateFiles(folder, "forge-*.jar")
            .Select(Path.GetFileName)
            .FirstOrDefault(n => n is not null && !n.Contains("installer", StringComparison.OrdinalIgnoreCase));
        if (oldForge is null) return null;

        var core = oldForge.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? oldForge[..^4] : oldForge;
        if (core.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) core = core[6..];
        if (core.EndsWith("-universal", StringComparison.OrdinalIgnoreCase)) core = core[..^"-universal".Length];
        var (oldGame, oldLoader) = SplitForgeId(core);
        return new ServerDetection
        {
            Type = ServerType.Forge, JarFile = oldForge, GameVersion = oldGame, LoaderVersion = oldLoader
        };
    }

    /// <summary>
    /// A Fabric server: the launcher this app downloads (<c>fabric-server.jar</c>), the one Fabric's
    /// own website hands out (<c>fabric-server-mc.1.21.1-loader.0.16.2-launcher.1.0.1.jar</c>), or
    /// the old installer's <c>fabric-server-launch.jar</c>.
    /// </summary>
    /// <remarks>
    /// The first two carry an <c>install.properties</c> with both versions in it. The old launcher
    /// does not; it runs a vanilla <c>server.jar</c> sitting next to it, so the game version is read
    /// from that one instead, and the loader version is simply not known.
    /// </remarks>
    private ServerDetection? TryFabric(string folder)
    {
        var candidates = Directory.EnumerateFiles(folder, "fabric-server*.jar")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n.Equals("fabric-server.jar", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (var name in candidates)
        {
            var (game, loader) = ReadFabricInstall(Path.Combine(folder, name));
            if (game is not null)
                return new ServerDetection
                {
                    Type = ServerType.Fabric, JarFile = name, GameVersion = game,
                    LoaderVersion = string.IsNullOrEmpty(loader) ? null : loader
                };

            if (name.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase))
                return new ServerDetection
                {
                    Type = ServerType.Fabric, JarFile = name,
                    GameVersion = _java.GetGameVersionFromJar(Path.Combine(folder, "server.jar"))
                };
        }
        return null;
    }

    private ServerDetection? TryPurpur(string folder) => ByJarPrefix(folder, "purpur", ServerType.Purpur);

    private ServerDetection? TryPaper(string folder) => ByJarPrefix(folder, "paper", ServerType.Paper);

    /// <summary>
    /// Paper and Purpur: a jar named after them in the root. Both are built on the vanilla server,
    /// so its <c>version.json</c> says which Minecraft it is.
    /// </summary>
    private ServerDetection? ByJarPrefix(string folder, string prefix, ServerType type)
    {
        var jar = Directory.EnumerateFiles(folder, prefix + "*.jar")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (jar is null) return null;

        return new ServerDetection
        {
            Type = type, JarFile = jar, GameVersion = _java.GetGameVersionFromJar(Path.Combine(folder, jar))
        };
    }

    /// <summary>
    /// A vanilla server: the jar the caller named, or <c>server.jar</c>, or failing both any jar
    /// in the root that says which Minecraft it is — the official download used to be called
    /// <c>minecraft_server.1.12.2.jar</c>.
    /// </summary>
    private ServerDetection? TryVanilla(string folder, string? preferredJar)
    {
        var tried = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredJar)) tried.Add(preferredJar);
        tried.Add("server.jar");
        tried.AddRange(RootJars(folder));

        foreach (var jar in tried.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var version = _java.GetGameVersionFromJar(Path.Combine(folder, jar));
            if (version is not null)
                return new ServerDetection { Type = ServerType.Vanilla, JarFile = jar, GameVersion = version };
        }
        return null;
    }

    // --- How it was set up ---

    /// <summary>
    /// The memory the folder's own launch files ask for: <c>user_jvm_args.txt</c> (Forge,
    /// NeoForge, and this app's own), then the usual start scripts.
    /// </summary>
    /// <remarks>
    /// Commented lines are skipped, and that is not a nicety: the <c>user_jvm_args.txt</c> Forge
    /// writes is mostly comments explaining the options, including an example <c>-Xmx4G</c> that
    /// nobody asked for.
    /// </remarks>
    internal static (int? Min, int? Max) ReadMemory(string folder)
    {
        foreach (var name in new[] { "user_jvm_args.txt", "run.bat", "run.sh", "start.bat", "start.sh" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { continue; }

            int? min = null, max = null;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || line.StartsWith("::", StringComparison.Ordinal)
                    || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (Match m in MemoryFlag().Matches(line))
                {
                    var gb = ToGb(long.Parse(m.Groups[2].Value), m.Groups[3].Value);
                    if (m.Groups[1].Value is "s" or "S") min ??= gb;
                    else max ??= gb;
                }
            }
            if (min is not null || max is not null) return (min, max);
        }
        return (null, null);
    }

    private static int ToGb(long amount, string unit) => unit.ToUpperInvariant() switch
    {
        "G" => (int)Math.Clamp(amount, 1, 1024),
        "M" => (int)Math.Clamp(Math.Round(amount / 1024.0), 1, 1024),
        _ => (int)Math.Clamp(Math.Round(amount / (1024.0 * 1024)), 1, 1024)   // K
    };

    [GeneratedRegex(@"-Xm([sx])(\d{1,7})([GgMmKk])\b")]
    private static partial Regex MemoryFlag();

    /// <summary>The jars in the root, installers left out: nobody means to launch one of those.</summary>
    private static IReadOnlyList<string> RootJars(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.jar")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.Contains("installer", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool HasArgsFile(string dir) =>
        File.Exists(Path.Combine(dir, "win_args.txt")) || File.Exists(Path.Combine(dir, "unix_args.txt"));

    /// <summary>"1.20.1-47.2.0" -> ("1.20.1", "47.2.0").</summary>
    private static (string game, string loader) SplitForgeId(string id)
    {
        var idx = id.IndexOf('-');
        return idx > 0 ? (id[..idx], id[(idx + 1)..]) : (id, string.Empty);
    }

    /// <summary>
    /// Reads game-version and fabric-loader-version from the fabric launcher jar's
    /// install.properties. Also used by <see cref="ModLoaderService"/> to structurally validate a
    /// freshly-downloaded Fabric server jar (Fabric's meta endpoint publishes no checksums).
    /// </summary>
    internal static (string? game, string? loader) ReadFabricInstall(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("install.properties");
            if (entry is null) return (null, null);

            using var sr = new StreamReader(entry.Open());
            string? game = null, loader = null, line;
            while ((line = sr.ReadLine()) is not null)
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var key = line[..i].Trim();
                var val = line[(i + 1)..].Trim();
                if (key == "game-version") game = val;
                else if (key == "fabric-loader-version") loader = val;
            }
            return (game, loader);
        }
        catch
        {
            return (null, null);
        }
    }
}
