using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Letting people join from a Minecraft version other than the one the server runs.
/// </summary>
/// <remarks>
/// <para>
/// Two plugins, and they are not interchangeable. <strong>ViaVersion</strong> admits clients
/// <em>newer</em> than the server, which is what happens the week a Minecraft version ships and
/// everyone updates before the server can. <strong>ViaBackwards</strong> admits clients
/// <em>older</em> than the server, which is the friend who never updates. Installing only the first
/// covers the rarer half and looks like the feature is broken for everyone else.
/// </para>
/// <para>
/// Deliberately independent of crossplay. Geyser does not need either of these, and a Java-only
/// server gets the whole benefit — this is about which versions may connect, not which edition.
/// </para>
/// </remarks>
public class MultiVersionService
{
    /// <summary>The two plugins, by Modrinth project and by the name people know them by.</summary>
    private static readonly (string Id, string Name)[] Plugins =
        [("viaversion", "ViaVersion"), ("viabackwards", "ViaBackwards")];

    /// <summary>Modrinth ids. Both publish for the <c>paper</c> loader, which covers the family.</summary>
    public static readonly string[] ProjectIds = Plugins.Select(p => p.Id).ToArray();

    private readonly ModrinthService _modrinth;

    public MultiVersionService(ModrinthService? modrinth = null) => _modrinth = modrinth ?? new ModrinthService();

    /// <summary>Whether this kind of server can take them at all.</summary>
    /// <remarks>
    /// Plugin family only. The mod-loader builds exist, but a modded server already refuses clients
    /// whose mods do not match, so version bridging there solves a problem the loader recreates.
    /// </remarks>
    public static bool CanEnable(ServerType type) => ServerTypeCatalog.IsPluginBased(type);

    /// <summary>
    /// Installs both plugins into the server, verified, reporting progress.
    /// </summary>
    /// <remarks>
    /// Both or neither, like Geyser and Floodgate: half of this is a server that quietly turns away
    /// the very players it was switched on for.
    /// </remarks>
    public async Task InstallAsync(ServerConfig config, IProgress<string>? log, CancellationToken ct = default)
    {
        var folder = ContentInstall.PrepareFolder(config, CanEnable(config.Type), "Msg_MultiVersionUnsupportedFmt");

        // By name in what the user reads: "No se pudo sustituir viaversion" read like a bug.
        foreach (var (id, name) in Plugins)
            await ContentInstall.InstallFromModrinthAsync(_modrinth, config, folder, id, name, log, ct);

        log?.Report(Localizer.Get("Msg_MultiVersionReady"));
    }
}
