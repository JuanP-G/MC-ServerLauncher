using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Fills in Type/GameVersion/loader info for servers saved before those fields existed
/// (i.e. GameVersion empty), by inspecting the server folder. Best-effort, runs on load so the
/// mods browser works for older Fabric/Forge servers.
/// </summary>
public class ServerDetectionService
{
    private readonly JavaService _java = new();

    /// <summary>Detects and fills missing fields in place. Returns true if it changed the config.</summary>
    public bool DetectAndFill(ServerConfig config)
    {
        if (!string.IsNullOrEmpty(config.GameVersion)) return false; // already known (new-style config)
        if (string.IsNullOrEmpty(config.FolderPath) || !Directory.Exists(config.FolderPath)) return false;

        return TryForge(config) || TryFabric(config) || TryPaper(config) || TryVanilla(config);
    }

    private bool TryPaper(ServerConfig config)
    {
        var paperJar = Directory.EnumerateFiles(config.FolderPath, "paper*.jar")
            .Select(Path.GetFileName)
            .FirstOrDefault(n => n is not null);
        if (paperJar is null) return false;

        config.Type = ServerType.Paper;
        config.JarFile = paperJar;
        // Paper jars are based on the vanilla server, so version.json is present.
        var version = _java.GetGameVersionFromJar(Path.Combine(config.FolderPath, paperJar));
        if (version is not null) config.GameVersion = version;
        return true;
    }

    private static bool TryForge(ServerConfig config)
    {
        // Modern Forge (1.17+): an args file under libraries/net/minecraftforge/forge/<id>/.
        var forgeRoot = Path.Combine(config.FolderPath, "libraries", "net", "minecraftforge", "forge");
        if (Directory.Exists(forgeRoot))
        {
            foreach (var dir in Directory.GetDirectories(forgeRoot))
            {
                if (!File.Exists(Path.Combine(dir, "win_args.txt")) && !File.Exists(Path.Combine(dir, "unix_args.txt")))
                    continue;
                var id = Path.GetFileName(dir);
                config.Type = ServerType.Forge;
                config.ForgeArgs = id;
                (config.GameVersion, config.ModLoaderVersion) = SplitForgeId(id);
                return true;
            }
        }

        // Old Forge (≤1.16.5): a runnable forge-*.jar in the root.
        var oldForge = Directory.EnumerateFiles(config.FolderPath, "forge-*.jar")
            .Select(Path.GetFileName)
            .FirstOrDefault(n => n is not null && !n.Contains("installer", StringComparison.OrdinalIgnoreCase));
        if (oldForge is not null)
        {
            var core = oldForge.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? oldForge[..^4] : oldForge;
            if (core.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) core = core[6..];
            config.Type = ServerType.Forge;
            config.JarFile = oldForge;
            (config.GameVersion, config.ModLoaderVersion) = SplitForgeId(core);
            return true;
        }
        return false;
    }

    private static bool TryFabric(ServerConfig config)
    {
        var fabricJar = Path.Combine(config.FolderPath, "fabric-server.jar");
        if (!File.Exists(fabricJar)) return false;

        var (game, loader) = ReadFabricInstall(fabricJar);
        if (game is null) return false;

        config.Type = ServerType.Fabric;
        config.JarFile = "fabric-server.jar";
        config.GameVersion = game;
        if (!string.IsNullOrEmpty(loader)) config.ModLoaderVersion = loader;
        return true;
    }

    private bool TryVanilla(ServerConfig config)
    {
        var version = _java.GetGameVersionFromJar(config.JarFullPath);
        if (version is null) return false;
        config.Type = ServerType.Vanilla;
        config.GameVersion = version;
        return true;
    }

    /// <summary>"1.20.1-47.2.0" -> ("1.20.1", "47.2.0").</summary>
    private static (string game, string loader) SplitForgeId(string id)
    {
        var idx = id.IndexOf('-');
        return idx > 0 ? (id[..idx], id[(idx + 1)..]) : (id, string.Empty);
    }

    /// <summary>Reads game-version and fabric-loader-version from the fabric launcher jar's install.properties.</summary>
    private static (string? game, string? loader) ReadFabricInstall(string jarPath)
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
