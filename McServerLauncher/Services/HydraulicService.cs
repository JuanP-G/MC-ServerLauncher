using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Hydraulic: what lets Bedrock players actually see the blocks and items that mods add.
/// </summary>
/// <remarks>
/// <para>
/// Geyser translates the protocol, so Bedrock players can reach a modded server — but everything a
/// mod adds is unknown to their client. Hydraulic, from GeyserMC too, converts that content into a
/// Bedrock resource pack. It is the answer to "the mods exist, this ought to be possible", and it
/// is: on <strong>Fabric</strong>.
/// </para>
/// <para>
/// Not on NeoForge. Hydraulic built for NeoForge until build 107 in February 2026; since then the
/// module is commented out of its <c>settings.gradle.kts</c> and every published build carries a
/// Fabric download only. Its one confirmed open bug is that the NeoForge client check was not
/// bypassed anyway. Offering it there would install a jar that cannot help.
/// </para>
/// <para>
/// Its authors call it early development, and the app says so rather than deciding for the user.
/// </para>
/// </remarks>
public class HydraulicService
{
    private const string Downloads = "https://download.geysermc.org/v2/projects/hydraulic";

    /// <summary>Modrinth id for Fabric API, which Hydraulic requires and nothing else installs.</summary>
    /// <remarks>
    /// Declared in Hydraulic's own <c>fabric.mod.json</c> as a hard dependency. Without it the mod
    /// is simply not loaded, and the server starts looking healthy while Bedrock players see the
    /// same untextured world as before.
    /// </remarks>
    public const string FabricApiProjectId = "fabric-api";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly ModrinthService _modrinth;

    public HydraulicService(ModrinthService? modrinth = null) => _modrinth = modrinth ?? new ModrinthService();

    /// <summary>Whether this server type can run Hydraulic at all.</summary>
    public static bool CanEnable(ServerType type) => type == ServerType.Fabric;

    /// <summary>A published build and the file it offers for Fabric.</summary>
    public record HydraulicBuild(int Build, string FileName, string Url, string Sha256);

    /// <summary>
    /// The newest build that publishes a Fabric download, or null when none does.
    /// </summary>
    /// <remarks>
    /// Walked backwards rather than taking the last build outright: builds are published per commit
    /// and a given one may carry no Fabric artifact at all. Taking the newest blindly is how the
    /// NeoForge situation was first misread.
    /// </remarks>
    public async Task<HydraulicBuild?> GetLatestBuildAsync(CancellationToken ct = default)
    {
        var versionsJson = await Http.GetStringAsync(Downloads, ct);
        using var project = JsonDocument.Parse(versionsJson);
        var version = project.RootElement.GetProperty("versions").EnumerateArray().Last().GetString();
        if (version is null) return null;

        var buildsJson = await Http.GetStringAsync($"{Downloads}/versions/{version}", ct);
        using var versionDoc = JsonDocument.Parse(buildsJson);
        var builds = versionDoc.RootElement.GetProperty("builds").EnumerateArray()
            .Select(b => b.GetInt32()).OrderByDescending(b => b).ToList();

        foreach (var build in builds.Take(10))
        {
            ct.ThrowIfCancellationRequested();

            var buildJson = await Http.GetStringAsync($"{Downloads}/versions/{version}/builds/{build}", ct);
            using var doc = JsonDocument.Parse(buildJson);

            if (!doc.RootElement.GetProperty("downloads").TryGetProperty("fabric", out var fabric)) continue;

            var name = fabric.TryGetProperty("name", out var n) ? n.GetString() : null;
            var sha256 = fabric.TryGetProperty("sha256", out var h) ? h.GetString() : null;
            if (name is null || string.IsNullOrEmpty(sha256)) continue;   // no checksum, no install

            return new HydraulicBuild(build, name,
                $"{Downloads}/versions/{version}/builds/{build}/downloads/fabric", sha256!);
        }

        return null;
    }

    /// <summary>
    /// Installs Fabric API and Hydraulic into the server, verified, or throws saying why not.
    /// </summary>
    /// <remarks>
    /// Fabric API first: if that is unavailable for this Minecraft version there is no point
    /// downloading forty megabytes of Hydraulic to sit unloaded beside it.
    /// </remarks>
    public async Task InstallAsync(ServerConfig config, IProgress<string>? log, CancellationToken ct = default)
    {
        if (!CanEnable(config.Type))
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_HydraulicUnsupportedFmt"), config.Type));

        var folder = Path.Combine(config.FolderPath, ServerTypeCatalog.ContentFolder(config.Type));
        Directory.CreateDirectory(folder);

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayResolvingFmt"), "Fabric API"));
        var api = await _modrinth.GetLatestProjectVersionAsync(FabricApiProjectId, config.Type, config.GameVersion, ct);
        var apiFile = api?.Files.FirstOrDefault();
        if (apiFile is null)
            throw new InvalidOperationException(string.Format(
                Localizer.Get("Msg_CrossplayNoVersionFmt"), "Fabric API", config.GameVersion, config.Type));

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), "Fabric API", api!.VersionNumber));
        await _modrinth.DownloadModAsync(apiFile.Url, Path.Combine(folder, apiFile.Filename),
            apiFile.Hashes?.Sha512, apiFile.Hashes?.Sha1, ct: ct);

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayResolvingFmt"), "Hydraulic"));
        var build = await GetLatestBuildAsync(ct)
            ?? throw new InvalidOperationException(Localizer.Get("Msg_HydraulicNoBuild"));

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), "Hydraulic", "b" + build.Build));

        using var resp = await Http.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await AtomicDownload.ToFileAsync(resp.Content, Path.Combine(folder, build.FileName),
            verifyAsync: async (part, token) =>
            {
                await DownloadVerifier.VerifyAsync(part, build.Sha256, HashAlgorithmName.SHA256, token);

                // Its downloads API has no per-Minecraft-version dimension: there is one line of
                // builds and the newest targets the newest Minecraft. The jar says which, so the
                // jar is asked before it is kept.
                var required = ReadRequiredMinecraft(part);
                if (required is not null && !MinecraftRange.Satisfies(config.GameVersion, required))
                    throw new InvalidOperationException(string.Format(
                        Localizer.Get("Msg_HydraulicWrongVersionFmt"), config.GameVersion, required));
            },
            ct: ct);

        log?.Report(Localizer.Get("Msg_HydraulicReady"));
    }

    /// <summary>The Minecraft range a Fabric mod declares, from its own metadata. Null if absent.</summary>
    internal static string? ReadRequiredMinecraft(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry is null) return null;

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);

            return doc.RootElement.TryGetProperty("depends", out var depends)
                   && depends.TryGetProperty("minecraft", out var mc)
                ? mc.GetString()
                : null;
        }
        catch
        {
            // Unreadable metadata is not proof of a mismatch; the checksum already proved the file
            // arrived intact, so let it through rather than blocking on a parsing detail.
            return null;
        }
    }
}
