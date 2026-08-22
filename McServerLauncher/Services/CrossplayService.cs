using System.Collections.Generic;
using System.IO;
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
/// Setting a server up so people can join from Bedrock as well as Java.
/// </summary>
/// <remarks>
/// <para>
/// Three separate things have to line up, which is why doing this by hand goes wrong. The server
/// needs <strong>Geyser</strong> to understand Bedrock clients at all, and <strong>Floodgate</strong>
/// so those players don't each need to own Minecraft: Java. It needs a <strong>second tunnel</strong>,
/// because Java is TCP and Bedrock is UDP and one cannot carry the other. And Geyser has to be told
/// the tunnel's <em>public</em> port, or the Bedrock server list advertises a port nobody can use.
/// </para>
/// <para>
/// Geyser comes from Modrinth through the app's existing install path, which already resolves the
/// right build for the loader and version and verifies its hash. Floodgate is split: Modrinth
/// carries the Fabric and NeoForge builds, and only GeyserMC's own downloads site carries the
/// Spigot one that Paper needs. Whichever the source, nothing is installed without its checksum.
/// </para>
/// </remarks>
public class CrossplayService
{
    /// <summary>Modrinth's id for Geyser: the Bedrock↔Java translator.</summary>
    public const string GeyserProjectId = "geyser";

    /// <summary>
    /// Modrinth's id for Floodgate, which lets Bedrock players in without a Java account.
    /// </summary>
    /// <remarks>
    /// Only usable for Fabric and NeoForge. Modrinth's "floodgate" project is
    /// <c>GeyserMC/Floodgate-Modded</c>, which is exactly those two loaders — the Spigot build that
    /// Paper needs is published only on GeyserMC's own downloads site, so Paper takes the other
    /// path below. Assuming one source covered all three was wrong, and it would have failed on the
    /// server type most people would pick for this.
    /// </remarks>
    public const string FloodgateProjectId = "floodgate";

    /// <summary>Where Floodgate for Spigot-family servers actually lives.</summary>
    private const string GeyserDownloads = "https://download.geysermc.org/v2/projects";

    /// <summary>Bedrock's default port. Only a starting point for the search.</summary>
    public const int DefaultBedrockPort = 19132;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly ModrinthService _modrinth;
    private readonly PortService _ports;

    public CrossplayService(ModrinthService? modrinth = null, PortService? ports = null)
    {
        _modrinth = modrinth ?? new ModrinthService();
        _ports = ports ?? new PortService();
    }

    /// <summary>Whether this server can do crossplay at all, and why not when it can't.</summary>
    public static bool CanEnable(ServerType type) => GeyserConfigService.Supports(type);

    /// <summary>
    /// Whether the mods installed on this kind of server can shut Bedrock players out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True for the mod loaders, false for Paper. Geyser joins the Java server as a client with no
    /// mods, and a mod loader carrying content the client is required to have refuses exactly that
    /// connection — so crossplay works the day it is switched on and stops working the day a mod
    /// adding blocks or items is installed, with nothing linking the two events.
    /// </para>
    /// <para>
    /// Paper is not affected because plugins run only on the server; a client with no plugins is
    /// the only kind there is. Which is why it is the honest recommendation for wanting both
    /// content and Bedrock players.
    /// </para>
    /// </remarks>
    public static bool ModsCanLockOutBedrock(ServerType type) =>
        type is ServerType.Fabric or ServerType.NeoForge;

    /// <summary>
    /// A free local UDP port for Geyser, starting at Bedrock's default.
    /// </summary>
    /// <remarks>
    /// Asked of the UDP table, not the TCP one. They are separate namespaces, so a port can be
    /// taken for TCP and free for UDP — and picking with the wrong table hands out a port something
    /// else already holds, which shows up only as a Geyser that fails to bind.
    /// </remarks>
    public int PickBedrockPort(IEnumerable<int> alsoAvoid) =>
        _ports.FindFreeUdpPort(DefaultBedrockPort, new HashSet<int>(alsoAvoid)) ?? DefaultBedrockPort;

    /// <summary>Whether Floodgate for this server comes from Modrinth or GeyserMC's own site.</summary>
    /// <remarks>
    /// Not a preference: Modrinth's Floodgate is the modded build and has nothing for Paper, while
    /// GeyserMC's downloads site has the Spigot build and nothing modded. Each type has exactly one
    /// source that works.
    /// </remarks>
    internal static bool FloodgateComesFromModrinth(ServerType type) =>
        type is ServerType.Fabric or ServerType.NeoForge;

    /// <summary>
    /// Downloads Geyser and Floodgate into the server, verified, and reports progress.
    /// </summary>
    /// <remarks>
    /// Both or neither: a server with Geyser but no Floodgate starts and accepts Bedrock
    /// connections, then rejects every player who hasn't bought Minecraft: Java — which looks like
    /// crossplay is broken rather than half-installed. Failing loudly is the better outcome.
    /// </remarks>
    public async Task InstallAsync(ServerConfig config, IProgress<string>? log, CancellationToken ct = default)
    {
        if (!CanEnable(config.Type))
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_CrossplayUnsupportedFmt"), config.Type));

        // Paper takes plugins, the mod loaders take mods. Same rule the mod store already uses.
        var folder = Path.Combine(config.FolderPath, config.Type == ServerType.Paper ? "plugins" : "mods");
        Directory.CreateDirectory(folder);

        await InstallFromModrinthAsync(config, folder, GeyserProjectId, "Geyser", log, ct);

        if (FloodgateComesFromModrinth(config.Type))
            await InstallFromModrinthAsync(config, folder, FloodgateProjectId, "Floodgate", log, ct);
        else
            await InstallFloodgateForSpigotAsync(folder, log, ct);
    }

    private async Task InstallFromModrinthAsync(ServerConfig config, string folder,
        string projectId, string display, IProgress<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log?.Report(string.Format(Localizer.Get("Msg_CrossplayResolvingFmt"), display));

        var version = await _modrinth.GetLatestProjectVersionAsync(projectId, config.Type, config.GameVersion, ct);
        var file = version?.Files.FirstOrDefault();
        if (file is null)
            throw new InvalidOperationException(string.Format(
                Localizer.Get("Msg_CrossplayNoVersionFmt"), display, config.GameVersion, config.Type));

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), display, version!.VersionNumber));
        await _modrinth.DownloadModAsync(
            file.Url, Path.Combine(folder, file.Filename), file.Hashes?.Sha512, file.Hashes?.Sha1, ct: ct);
    }

    /// <summary>
    /// Floodgate for Paper, from GeyserMC's own downloads site.
    /// </summary>
    /// <remarks>
    /// Their API returns the SHA-256 alongside the download, which is what makes this acceptable:
    /// the jar is verified before it is kept, exactly like everything else the app installs.
    /// </remarks>
    private async Task InstallFloodgateForSpigotAsync(string folder, IProgress<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log?.Report(string.Format(Localizer.Get("Msg_CrossplayResolvingFmt"), "Floodgate"));

        var (version, build, fileName, sha256) = await LatestSpigotFloodgateAsync(ct);

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), "Floodgate", $"{version}-b{build}"));

        var url = $"{GeyserDownloads}/floodgate/versions/{version}/builds/{build}/downloads/spigot";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await AtomicDownload.ToFileAsync(resp.Content, Path.Combine(folder, fileName),
            verifyAsync: (part, token) =>
                DownloadVerifier.VerifyAsync(part, sha256, HashAlgorithmName.SHA256, token),
            ct: ct);
    }

    private static async Task<(string Version, int Build, string FileName, string Sha256)>
        LatestSpigotFloodgateAsync(CancellationToken ct)
    {
        var projectJson = await Http.GetStringAsync($"{GeyserDownloads}/floodgate", ct);
        using var project = JsonDocument.Parse(projectJson);
        var version = project.RootElement.GetProperty("versions").EnumerateArray().Last().GetString()!;

        var versionJson = await Http.GetStringAsync($"{GeyserDownloads}/floodgate/versions/{version}", ct);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var build = versionDoc.RootElement.GetProperty("builds").EnumerateArray().Last().GetInt32();

        var buildJson = await Http.GetStringAsync($"{GeyserDownloads}/floodgate/versions/{version}/builds/{build}", ct);
        using var buildDoc = JsonDocument.Parse(buildJson);

        if (!buildDoc.RootElement.GetProperty("downloads").TryGetProperty("spigot", out var spigot))
            throw new InvalidOperationException(string.Format(
                Localizer.Get("Msg_CrossplayNoVersionFmt"), "Floodgate", version, ServerType.Paper));

        var name = spigot.GetProperty("name").GetString() ?? "floodgate-spigot.jar";
        var sha256 = spigot.TryGetProperty("sha256", out var h) ? h.GetString() : null;

        // Same rule as everywhere else: no checksum, no install.
        if (string.IsNullOrEmpty(sha256))
            throw new InvalidOperationException(Localizer.Get("Msg_CrossplayNoChecksum"));

        return (version, build, name, sha256);
    }

    /// <summary>
    /// Writes Geyser's local port and, when the server is tunnelled, the public port it must
    /// advertise. Safe to call repeatedly: it patches what is there rather than replacing it.
    /// </summary>
    /// <param name="config">The server being configured.</param>
    /// <param name="publicPort">
    /// The tunnel's public port, or null when there is no tunnel — on a local network the port
    /// players use is the local one, and advertising anything else would be wrong.
    /// </param>
    public void WriteConfig(ServerConfig config, int? publicPort)
    {
        var path = GeyserConfigService.ConfigPath(config.FolderPath, config.Type);
        if (path is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var port = config.BedrockPort > 0 ? config.BedrockPort : DefaultBedrockPort;

        // Written before Geyser has ever run when it has to be: Geyser fills in everything else on
        // first start, so crossplay works on that first start instead of only after a restart.
        var yaml = File.Exists(path)
            ? GeyserConfigService.SetBedrockPorts(File.ReadAllText(path), port, publicPort)
            : GeyserConfigService.MinimalConfig(port, publicPort);

        File.WriteAllText(path, yaml);
    }
}
