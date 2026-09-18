using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// What a mod or plugin jar says about itself: the names it provides, and the ones it needs.
/// </summary>
/// <remarks>
/// <para>
/// The jar is the authority, not the store. Verified live in August 2026: Explorify declares no
/// dependencies at all on Modrinth while its own <c>fabric.mod.json</c> requires <c>fabric-api</c>,
/// which is exactly the crash that started this. Asking the store also means asking the network,
/// and the two calls that would answer swallow every error and return an empty result — offline,
/// the app would cheerfully report that nothing is missing right before the server failed to boot.
/// Reading the jars answers the same question in milliseconds, with no network and no lying.
/// </para>
/// <para>
/// Three formats, because "all your mods and plugins" is not true otherwise: Fabric's
/// <c>fabric.mod.json</c>, Bukkit's <c>plugin.yml</c> (which Paper and Purpur use), and
/// Forge/NeoForge's <c>mods.toml</c>. Only the handful of keys that name things is parsed — no YAML
/// or TOML library is added for it, the same choice already made for Geyser's config. Anything this
/// does not understand reads as "declares nothing", which is the safe direction: the check stays
/// quiet rather than blocking a start over something it misread.
/// </para>
/// </remarks>
public static class ContentManifest
{
    /// <summary>One jar: what it offers under what names, and what it needs to be there.</summary>
    /// <param name="FileName">The jar's own file name, for saying who needs what.</param>
    /// <param name="Provides">Every id this jar answers to, including its own.</param>
    /// <param name="Requires">Ids that must be present for it to load.</param>
    public record Manifest(string FileName, IReadOnlyList<string> Provides, IReadOnlyList<string> Requires)
    {
        /// <summary>
        /// Where the jar says it runs, if it says.
        /// </summary>
        /// <remarks>
        /// An <c>init</c> property with a default rather than a fourth positional parameter, so
        /// that not one existing call site or test has to change to gain it. Everything that does
        /// not set it keeps reading <see cref="ContentSide.Unspecified"/>, which is what the jars
        /// that say nothing mean anyway.
        /// </remarks>
        public ContentSide Side { get; init; } = ContentSide.Unspecified;

        public static Manifest Empty(string fileName) =>
            new(fileName, Array.Empty<string>(), Array.Empty<string>());

        /// <summary>True when the jar said nothing this app can act on.</summary>
        public bool IsSilent => Provides.Count == 0 && Requires.Count == 0;
    }

    /// <summary>
    /// Where a jar declares it runs, read out of its own manifest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Unspecified"/> and <see cref="Both"/> are kept apart because they are different
    /// things to have read in a file, not because one is worth more than the other. Measured
    /// against a real modpack, <c>environment: "*"</c> turned out to carry no information at all:
    /// eleven jars out of eleven declared it, Floodgate among them, which is a Bedrock
    /// authentication plugin with nothing to do on a client. It is what the template writes.
    /// <c>ExportSelection</c> therefore treats the two identically, and only <c>client</c> and
    /// <c>server</c> count as anybody having said anything.
    /// </para>
    /// <para>
    /// Anything unrecognised reads as <see cref="Unspecified"/>, like every other value this file
    /// does not understand.
    /// </para>
    /// </remarks>
    public enum ContentSide
    {
        /// <summary>The jar did not say. Not the same as having said "both".</summary>
        Unspecified,

        /// <summary>The jar says it runs on both sides.</summary>
        Both,

        /// <summary>The jar says it is client-side only.</summary>
        Client,

        /// <summary>The jar says it is server-side only.</summary>
        Server
    }

    /// <summary>
    /// Ids the loader itself supplies, which no download could ever satisfy.
    /// </summary>
    /// <remarks>
    /// One set for every loader rather than one per format. A Fabric jar never asks for
    /// <c>neoforge</c>, so the extra names cost nothing, and keeping a single list means a loader
    /// name can never be missed in one reader and handled in another.
    /// <para>
    /// <c>mixinextras</c> is here because the loaders ship it inside themselves — Fabric Loader has
    /// since 0.15, and so do current Forge and NeoForge — so it lives in the server's
    /// <c>libraries/</c> and never in <c>mods/</c>. Lithium asks for it by name, and the check
    /// reported it missing on servers where the loader's own startup log lists it as loaded.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> LoaderProvided = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric",      // Fabric
        "mixinextras",                                       // bundled by every current loader
        "forge", "neoforge", "fml",                          // Forge and NeoForge
        "bukkit", "spigot", "paper", "purpur", "server"      // Bukkit and its descendants
    };

    /// <summary>Reads a jar's manifest. Never throws: an unreadable jar declares nothing.</summary>
    public static Manifest Read(string jarPath)
    {
        var name = Path.GetFileName(jarPath);
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            return FromArchive(zip, name, depth: 0);
        }
        catch
        {
            // A jar that cannot be opened is a problem for the loader to report, not a reason for
            // this app to refuse to start the server.
            return Manifest.Empty(name);
        }
    }

    /// <summary>
    /// How deep to follow jars inside jars. Real mods go one level down, occasionally two.
    /// </summary>
    private const int MaxNesting = 3;

    /// <summary>A nested jar bigger than this is not metadata worth reading into memory.</summary>
    private const long MaxNestedBytes = 64L * 1024 * 1024;

    /// <summary>One archive's manifest, with everything bundled inside it counted as provided.</summary>
    /// <remarks>
    /// <para>
    /// Both loaders let a mod carry other mods inside itself — Fabric under <c>META-INF/jars/</c>,
    /// Forge and NeoForge under <c>META-INF/jarjar/</c> — and load them as if they had been
    /// installed separately. <c>fabric-api</c> is forty-odd modules shipped that way, and Xaero's
    /// minimap carries <c>xaerolib</c> in its own jar. Reading only the outer manifest reported every
    /// one of those as missing: a server that started perfectly was told, before starting, that six
    /// dependencies had to be installed by hand, and the store was then asked for ids it could never
    /// resolve because they are modules, not projects.
    /// </para>
    /// <para>
    /// What a bundled jar <em>provides</em> is added; what it <em>requires</em> is not. The author
    /// ships the bundle as a unit, and a gap inside it is the loader's to report — pulling nested
    /// requirements in would invent missing dependencies, which is the very failure this fixes.
    /// </para>
    /// </remarks>
    private static Manifest FromArchive(ZipArchive zip, string name, int depth)
    {
        // In the order they are likely to be found, and stopping at the first that says anything:
        // a jar shipping two formats is one jar, not two.
        var own = FromFabric(zip, name)
            ?? FromPluginYml(zip, name)
            ?? FromModsToml(zip, name)
            ?? Manifest.Empty(name);

        if (depth >= MaxNesting) return own;

        var bundled = zip.Entries
            .Where(IsNestedJar)
            .SelectMany(entry => ProvidedByNested(entry, depth + 1))
            .ToList();

        if (bundled.Count == 0) return own;

        var provides = own.Provides
            .Concat(bundled)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A jar that needs something it carries itself is not waiting on anything.
        var requires = own.Requires
            .Where(r => !provides.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return own with { Provides = provides, Requires = requires };
    }

    /// <summary>Where the two loaders put the jars they carry inside another jar.</summary>
    /// <remarks>
    /// Matched on the location both loaders use rather than on each one's index file, so a nested
    /// jar is found whichever format listed it — and one the index forgot is still counted, which
    /// errs towards reporting less missing, the safe direction for a check that can block a start.
    /// </remarks>
    private static bool IsNestedJar(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
        (entry.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) ||
         entry.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids a jar inside a jar answers to. Empty if it cannot be read.</summary>
    private static IReadOnlyList<string> ProvidedByNested(ZipArchiveEntry entry, int depth)
    {
        if (entry.Length > MaxNestedBytes) return Array.Empty<string>();

        try
        {
            // ZipArchive needs a seekable stream, and an entry's is not.
            using var buffer = new MemoryStream();
            using (var source = entry.Open()) source.CopyTo(buffer);
            buffer.Position = 0;

            using var inner = new ZipArchive(buffer, ZipArchiveMode.Read);
            return FromArchive(inner, Path.GetFileName(entry.FullName), depth).Provides;
        }
        catch
        {
            // Same rule as the outer jar: unreadable means it declares nothing.
            return Array.Empty<string>();
        }
    }

    /// <summary>Every enabled jar in a server's content folder.</summary>
    /// <remarks>
    /// <c>.jar.disabled</c> files are skipped, and that is the whole point of the extension: a
    /// disabled mod neither provides anything nor needs anything, and counting it would both hide a
    /// real gap and invent a fake one.
    /// </remarks>
    public static IReadOnlyList<Manifest> ReadFolder(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<Manifest>();

        return Directory.EnumerateFiles(folder, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(Read)
            .ToList();
    }

    /// <summary>The content folder of a server, whether it takes mods or plugins.</summary>
    public static string FolderOf(ServerConfig config) =>
        Path.Combine(config.FolderPath, ServerTypeCatalog.ContentFolder(config.Type));

    // --- Fabric: fabric.mod.json ---

    private static Manifest? FromFabric(ZipArchive zip, string fileName)
    {
        var entry = zip.GetEntry("fabric.mod.json");
        if (entry is null) return null;

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var provides = new List<string>();
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            provides.Add(id.GetString()!);

        // "provides" is how a mod says it also answers to another id — a fork standing in for the
        // original, or one jar carrying what used to be several. Ignoring it invents missing
        // dependencies for mods that are in fact perfectly satisfied.
        if (root.TryGetProperty("provides", out var alias) && alias.ValueKind == JsonValueKind.Array)
            provides.AddRange(alias.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!));

        // "depends" only. "recommends" and "suggests" are the author's advice, and blocking a start
        // over advice would make the check something people turn off.
        var requires = new List<string>();
        if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
            requires.AddRange(depends.EnumerateObject().Select(p => p.Name));

        // "environment" is Fabric's own word for the side. The entrypoints are deliberately not
        // consulted as well: a jar with both a client and a server entrypoint is a jar that runs on
        // both, which is what the absence of an "environment" already says.
        var side = root.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.String
            ? env.GetString() switch
            {
                "*" => ContentSide.Both,
                "client" => ContentSide.Client,
                "server" => ContentSide.Server,
                _ => ContentSide.Unspecified
            }
            : ContentSide.Unspecified;

        return Build(fileName, provides, requires) with { Side = side };
    }

    // --- Bukkit, Paper, Purpur: plugin.yml ---

    private static Manifest? FromPluginYml(ZipArchive zip, string fileName)
    {
        var entry = zip.GetEntry("plugin.yml") ?? zip.GetEntry("paper-plugin.yml");
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();

        var provides = new List<string>();
        if (ScalarValue(text, "name") is { } name) provides.Add(name);

        // "provides" exists in plugin.yml too, and means the same thing it does in Fabric.
        provides.AddRange(ListValue(text, "provides"));

        // "depend" blocks loading; "softdepend" only asks to be loaded later if present, so a
        // missing one is not a failure and must not stop a start.
        //
        // Server by definition, and the one side that needs no parsing to know: a Bukkit plugin
        // runs in the server and there is no client half of the platform for it to run in.
        return Build(fileName, provides, ListValue(text, "depend")) with { Side = ContentSide.Server };
    }

    // --- Forge and NeoForge: mods.toml ---

    private static Manifest? FromModsToml(ZipArchive zip, string fileName)
    {
        var entry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open());
        var lines = reader.ReadToEnd().Split('\n');

        var provides = new List<string>();
        var requires = new List<string>();
        var side = ContentSide.Unspecified;

        // A tiny state machine over the two table headers that matter. Everything else is skipped,
        // including values spanning lines, which none of the keys read here ever do.
        var inMods = false;
        var inDependency = false;
        string? dependencyId = null;
        var dependencyRequired = true;   // both formats default a dependency to mandatory

        void FlushDependency()
        {
            if (dependencyId is { Length: > 0 } && dependencyRequired) requires.Add(dependencyId);
            dependencyId = null;
            dependencyRequired = true;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('['))
            {
                FlushDependency();
                inMods = line.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase);
                inDependency = line.StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inMods && TomlKey(line, "modId") is { } own) provides.Add(own);

            // Only inside [[mods]]. "side" appears in [[dependencies.x]] as well, where it means
            // which side that dependency is needed on — read there, a mod that merely requires a
            // server-side library would be filed as server-side itself.
            //
            // displayTest is not read, on purpose. It controls the version handshake, not where the
            // mod loads; it correlates with being server-side without ever asserting it, and acting
            // on a correlation is the one mistake this function cannot afford to make.
            if (inMods && TomlKey(line, "side") is { } declared)
                side = declared.ToUpperInvariant() switch
                {
                    "CLIENT" => ContentSide.Client,
                    "SERVER" => ContentSide.Server,
                    "BOTH" => ContentSide.Both,
                    _ => ContentSide.Unspecified
                };

            if (!inDependency) continue;

            if (TomlKey(line, "modId") is { } needed) dependencyId = needed;

            // NeoForge writes type = "required"; older Forge writes mandatory = true. Both appear
            // in the wild, and a jar built for either has to be read correctly by the same code.
            if (TomlKey(line, "type") is { } type)
                dependencyRequired = type.Equals("required", StringComparison.OrdinalIgnoreCase);
            else if (TomlBool(line, "mandatory") is { } mandatory)
                dependencyRequired = mandatory;
        }

        FlushDependency();
        return Build(fileName, provides, requires) with { Side = side };
    }

    // --- Shared ---

    private static Manifest Build(string fileName, IEnumerable<string> provides, IEnumerable<string> requires)
    {
        var offered = provides
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var needed = requires
            .Select(r => r.Trim())
            .Where(r => r.Length > 0 && !LoaderProvided.Contains(r))
            // A jar that lists itself is not waiting on anything.
            .Where(r => !offered.Contains(r, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Manifest(fileName, offered, needed);
    }

    /// <summary>A <c>key: value</c> at the top level of a YAML document.</summary>
    /// <remarks>
    /// Top level only, on purpose. <c>name</c> also appears inside <c>commands:</c> and
    /// <c>permissions:</c> blocks, and taking the first match anywhere in the file would read a
    /// command's name as the plugin's. Indentation is the only thing separating them.
    /// </remarks>
    private static string? ScalarValue(string yaml, string key)
    {
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(key.Length + 1)..].Trim().Trim('"', '\'');
            return value.Length > 0 ? value : null;
        }
        return null;
    }

    /// <summary>
    /// A top-level YAML list, written either inline or as indented dashes.
    /// </summary>
    /// <remarks>
    /// Both spellings are equally common in real plugin.yml files — <c>depend: [Vault]</c> and a
    /// block of <c>- Vault</c> lines under <c>depend:</c> — so reading only one of them would miss
    /// half the plugins for no reason a user could ever guess.
    /// </remarks>
    private static IReadOnlyList<string> ListValue(string yaml, string key)
    {
        var found = new List<string>();
        var lines = yaml.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;

            var inline = line[(key.Length + 1)..].Trim();
            if (inline.StartsWith('[') && inline.EndsWith(']'))
            {
                found.AddRange(inline[1..^1]
                    .Split(',')
                    .Select(p => p.Trim().Trim('"', '\''))
                    .Where(p => p.Length > 0));
                break;
            }

            // A block list: indented "- item" lines until something stops being indented.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var item = lines[j].TrimEnd('\r');
                if (item.Trim().Length == 0) continue;
                if (!char.IsWhiteSpace(item[0])) break;

                var trimmed = item.Trim();
                if (!trimmed.StartsWith('-')) break;

                var value = trimmed[1..].Trim().Trim('"', '\'');
                if (value.Length > 0) found.Add(value);
            }
            break;
        }

        return found;
    }

    /// <summary>A quoted TOML string value for a key, or null.</summary>
    private static string? TomlKey(string line, string key)
    {
        var value = TomlRawValue(line, key);
        if (value is null) return null;

        value = value.Trim().Trim('"', '\'');
        return value.Length > 0 ? value : null;
    }

    /// <summary>A TOML boolean value for a key, or null.</summary>
    private static bool? TomlBool(string line, string key) =>
        bool.TryParse(TomlRawValue(line, key)?.Trim(), out var parsed) ? parsed : null;

    private static string? TomlRawValue(string line, string key)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return null;
        if (!line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return null;

        // Trailing comments are legal TOML and common in generated mods.toml files.
        var value = line[(eq + 1)..];
        var hash = value.IndexOf('#');
        return hash >= 0 ? value[..hash] : value;
    }
}
