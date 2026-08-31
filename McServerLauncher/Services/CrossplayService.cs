using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

    /// <summary>Bedrock's default port. Only a starting point for the search.</summary>
    public const int DefaultBedrockPort = 19132;

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
        ServerTypeCatalog.For(type).Family == ServerFamily.Mods;

    /// <summary>
    /// The resx key of the caveat to show beside the crossplay checkbox, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Two different caveats, because the two mod loaders are in genuinely different positions and
    /// one paragraph covering both said less about each. Fabric has an answer — the content
    /// checkbox — and NeoForge does not, so its note says what to expect instead of what to tick.
    /// The plugin types get nothing: there is no caveat, and inventing one would be noise.
    /// </remarks>
    public static string? CaveatKey(ServerType type) => ServerTypeCatalog.Crossplay(type) switch
    {
        CrossplayLevel.Partial => "Crossplay_PartialNote",
        CrossplayLevel.Full when ModsCanLockOutBedrock(type) => "Crossplay_ModdedNote",
        _ => null
    };

    /// <summary>
    /// A free local UDP port for Geyser, starting at Bedrock's default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of the UDP table, not the TCP one. They are separate namespaces, so a port can be
    /// taken for TCP and free for UDP — and picking with the wrong table hands out a port something
    /// else already holds, which shows up only as a Geyser that fails to bind.
    /// </para>
    /// <para>
    /// Three sources, because two were not enough. The UDP table only knows what is bound
    /// <em>right now</em>, so a stopped server's port looks free; the other servers' ports cover
    /// the ones this app knows about; and <paramref name="accountTunnels"/> covers the rest — a
    /// tunnel left behind by a deleted server, one made by hand, or one belonging to another
    /// machine on the same playit account. Without that third list the port looks free, and
    /// creating a tunnel on it silently adopts somebody else's.
    /// </para>
    /// </remarks>
    /// <param name="serverPorts">Bedrock ports the app's other servers already hold.</param>
    /// <param name="accountTunnels">Local ports of the UDP tunnels on the playit account.</param>
    /// <param name="log">Where to explain a port that was skipped, or a search that could not look.</param>
    public int PickBedrockPort(IEnumerable<int> serverPorts, IEnumerable<int> accountTunnels,
        IProgress<string>? log = null)
    {
        var taken = new HashSet<int>(serverPorts);
        var fromAccount = new HashSet<int>(accountTunnels);
        taken.UnionWith(fromAccount);

        var port = _ports.FindFreeUdpPort(DefaultBedrockPort, taken, out var systemPortsRead)
                   ?? DefaultBedrockPort;

        if (!systemPortsRead)
            log?.Report(Localizer.Get("Msg_UdpTableUnreadable"));
        else if (port != DefaultBedrockPort && fromAccount.Contains(DefaultBedrockPort))
            log?.Report(string.Format(Localizer.Get("Msg_BedrockPortTakenByTunnelFmt"),
                DefaultBedrockPort, port));

        return port;
    }

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
        var folder = Path.Combine(config.FolderPath, ServerTypeCatalog.ContentFolder(config.Type));
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
            file.Url, AtomicDownload.PathIn(folder, file.Filename), file.Hashes?.Sha512, file.Hashes?.Sha1, ct: ct);
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

        var artifact = await GeyserDownloadsApi.LatestAsync("floodgate", "spigot", ct)
            ?? throw new InvalidOperationException(string.Format(
                Localizer.Get("Msg_CrossplayNoVersionFmt"), "Floodgate", "?", ServerType.Paper));

        log?.Report(string.Format(Localizer.Get("Msg_CrossplayInstallingFmt"), "Floodgate",
            $"{artifact.Version}-b{artifact.Build}"));

        using var resp = await GeyserDownloadsApi.OpenAsync(artifact, ct);
        resp.EnsureSuccessStatusCode();

        await AtomicDownload.ToFileAsync(resp.Content, AtomicDownload.PathIn(folder, artifact.FileName),
            verifyAsync: (part, token) =>
                DownloadVerifier.VerifyAsync(part, artifact.Sha256, HashAlgorithmName.SHA256, token),
            ct: ct);
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
        var floodgate = IsFloodgateInstalled(config);

        // Written before Geyser has ever run when it has to be: Geyser fills in everything else on
        // first start, so crossplay works on that first start instead of only after a restart.
        var yaml = File.Exists(path)
            ? GeyserConfigService.SetBedrockPorts(File.ReadAllText(path), port, publicPort, floodgate)
            : GeyserConfigService.MinimalConfig(port, publicPort, floodgate);

        yaml = GeyserConfigService.SetJavaAuth(yaml, floodgate, FloodgateKeyPath(config));

        // Only when it changed. This is called from the address refresh, which runs every thirty
        // seconds for as long as the server is up, and the answer is almost always the same file.
        AtomicTextFile.WriteIfChanged(path, yaml);
    }

    /// <summary>
    /// Corrects a Geyser config the app wrote badly, and says what it changed. Null when nothing did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existing servers need this, not just new ones: a config written before this was understood
    /// sits on <c>auth-type: online</c> for ever, Geyser tries to authenticate against Mojang with
    /// no account, and every Bedrock player is turned away with Floodgate asking whether it is
    /// configured correctly. Nothing about that resolves itself.
    /// </para>
    /// <para>
    /// Only the settings the app itself is responsible for are touched, and the file is left alone
    /// when they already say the right thing — so running it on every start costs nothing and
    /// cannot fight a config somebody fixed by hand.
    /// </para>
    /// </remarks>
    public string? RepairConfig(ServerConfig config)
    {
        var path = GeyserConfigService.ConfigPath(config.FolderPath, config.Type);
        if (path is null || !File.Exists(path)) return null;

        var before = File.ReadAllText(path);
        var floodgate = IsFloodgateInstalled(config);
        var after = GeyserConfigService.SetJavaAuth(before, floodgate, FloodgateKeyPath(config));

        if (!AtomicTextFile.WriteIfChanged(path, after)) return null;

        return string.Format(Localizer.Get("Msg_GeyserConfigRepairedFmt"),
            floodgate ? "floodgate" : "online");
    }

    /// <summary>Whether a Floodgate jar is sitting in the server's content folder.</summary>
    /// <remarks>
    /// Asked of the folder rather than remembered in the config: someone can delete the jar by hand,
    /// and telling Geyser to authenticate against a Floodgate that is not there turns every Bedrock
    /// player away.
    /// </remarks>
    public static bool IsFloodgateInstalled(ServerConfig config)
    {
        var folder = Path.Combine(config.FolderPath, ServerTypeCatalog.ContentFolder(config.Type));
        if (!Directory.Exists(folder)) return false;

        return Directory.EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .Any(n => n is not null
                   && n.StartsWith("floodgate", StringComparison.OrdinalIgnoreCase)
                   && n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where Floodgate leaves its key, as Geyser needs to see it: relative to Geyser's own folder.
    /// </summary>
    /// <remarks>
    /// Geyser's comment says a plugin Floodgate is picked up automatically. The mod version is not:
    /// it writes to <c>config/floodgate/key.pem</c> while Geyser looks beside its own config and
    /// finds nothing. The relative path is what Geyser resolves, so that is what gets written.
    /// </remarks>
    internal static string? FloodgateKeyPath(ServerConfig config)
    {
        var geyserConfig = GeyserConfigService.ConfigPath(config.FolderPath, config.Type);
        if (geyserConfig is null) return null;

        var geyserDir = Path.GetDirectoryName(geyserConfig)!;
        var key = Path.Combine(config.FolderPath, "config", "floodgate", "key.pem");

        // Forward slashes: this goes into a YAML value that Geyser reads on every platform, and a
        // Windows backslash in there is an escape waiting to be misread.
        return Path.GetRelativePath(geyserDir, key).Replace('\\', '/');
    }
}
