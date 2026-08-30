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
    /// <summary>Modrinth ids. Both publish for the <c>paper</c> loader, which covers the family.</summary>
    public static readonly string[] ProjectIds = { "viaversion", "viabackwards" };

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
        if (!CanEnable(config.Type))
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_MultiVersionUnsupportedFmt"), config.Type));

        var folder = Path.Combine(config.FolderPath, ServerTypeCatalog.ContentFolder(config.Type));
        Directory.CreateDirectory(folder);

        foreach (var projectId in ProjectIds)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report(string.Format(Localizer.Get("Msg_CrossplayResolvingFmt"), projectId));

            var version = await _modrinth.GetLatestProjectVersionAsync(projectId, config.Type, config.GameVersion, ct);
            var file = version?.Files.FirstOrDefault();
            if (file is null)
                throw new InvalidOperationException(string.Format(
                    Localizer.Get("Msg_CrossplayNoVersionFmt"), projectId, config.GameVersion, config.Type));

            log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), projectId, version!.VersionNumber));
            await _modrinth.DownloadModAsync(
                file.Url, Path.Combine(folder, file.Filename), file.Hashes?.Sha512, file.Hashes?.Sha1, ct: ct);
        }

        log?.Report(Localizer.Get("Msg_MultiVersionReady"));
    }
}
