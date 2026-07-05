using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

public class ModLoaderService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static ModLoaderService()
    {
        // Some CDNs (Forge's maven included) reject requests without a User-Agent.
        Http.DefaultRequestHeaders.Add("User-Agent", "JuanP-G/MC-ServerLauncher");
    }

    public async Task<string> GetLatestFabricLoaderVersionAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync("https://meta.fabricmc.net/v2/versions/loader", ct);
        using var doc = JsonDocument.Parse(json);
        
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("version", out var versionProp) && 
                element.TryGetProperty("stable", out var stableProp) && 
                stableProp.GetBoolean())
            {
                return versionProp.GetString() ?? string.Empty;
            }
        }
        
        return "0.16.2"; // Fallback just in case
    }

    /// <summary>
    /// Downloads the Fabric server launcher jar. KNOWN LIMITATION: unlike Mojang/Paper/Adoptium/
    /// Modrinth, Fabric's meta endpoint publishes no checksum for this artifact, so a true
    /// integrity check isn't possible without changing the source; TLS is the only transport
    /// protection. As a structural safety net the downloaded jar is opened and required to carry
    /// the install.properties every Fabric server launcher embeds, with exactly the game/loader
    /// versions that were requested — a corrupted or swapped file is deleted and rejected instead
    /// of being executed later.
    /// </summary>
    public async Task DownloadFabricServerAsync(string gameVersion, string loaderVersion, string destPath, IProgress<string>? log, CancellationToken ct = default)
    {
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}/{loaderVersion}/1.0.0/server/jar";

        log?.Report(Localizer.Get("Msg_DownloadingJar"));

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        if (totalMb > 0)
        {
            log?.Report(string.Format(Localizer.Get("Msg_DownloadingJarSize"), totalMb.ToString("0.#")));
        }

        await using (var fs = File.Create(destPath))
            await resp.Content.CopyToAsync(fs, ct);

        log?.Report(Localizer.Get("Msg_VerifyingJarStructure"));
        ValidateFabricServerJar(destPath, gameVersion, loaderVersion);

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
    }

    /// <summary>
    /// Structural validation of a downloaded Fabric server jar (see
    /// <see cref="DownloadFabricServerAsync"/>): it must be a readable jar whose
    /// install.properties matches the requested game and loader versions. On failure the file is
    /// deleted (so a bad download can't linger and get executed) and an exception is thrown.
    /// </summary>
    public static void ValidateFabricServerJar(string jarPath, string expectedGameVersion, string expectedLoaderVersion)
    {
        // ReadFabricInstall swallows unreadable/corrupt jars into (null, null), which fails below.
        var (game, loader) = ServerDetectionService.ReadFabricInstall(jarPath);

        if (!string.Equals(game, expectedGameVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(loader, expectedLoaderVersion, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(jarPath);
            throw new InvalidOperationException(string.Format(
                Localizer.Get("Msg_FabricJarInvalidFmt"), expectedGameVersion, expectedLoaderVersion));
        }
    }

    // --- Forge ---

    /// <summary>Outcome of a Forge server install: a runnable jar (old Forge) or an args id (modern Forge).</summary>
    public record ForgeInstallResult(string? JarFile, string? ArgsId);

    /// <summary>
    /// Recommended (or latest) Forge version for a Minecraft version, from Forge's promotions feed.
    /// Returns null if there is no Forge build for that version.
    /// </summary>
    public async Task<string?> GetRecommendedForgeVersionAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("promos", out var promos))
            return null;

        if (promos.TryGetProperty($"{mcVersion}-recommended", out var rec))
            return rec.GetString();
        if (promos.TryGetProperty($"{mcVersion}-latest", out var latest))
            return latest.GetString();
        return null;
    }

    /// <summary>
    /// Downloads the Forge installer and runs it with <c>--installServer</c> in <paramref name="folder"/>.
    /// Detects the launch method: modern Forge (1.17+) yields an args file (returns its id), older Forge
    /// yields a runnable <c>forge-*.jar</c> (returns its file name).
    /// </summary>
    public async Task<ForgeInstallResult> InstallForgeServerAsync(string folder, string mcVersion,
        string forgeVersion, string javaPath, IProgress<string>? log, CancellationToken ct = default)
    {
        var fullId = $"{mcVersion}-{forgeVersion}";
        var installerUrl =
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{fullId}/forge-{fullId}-installer.jar";
        var installerPath = Path.Combine(folder, $"forge-{fullId}-installer.jar");

        log?.Report(string.Format(Localizer.Get("Msg_ForgeDownloadingInstaller"), forgeVersion));
        using (var resp = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(installerPath);
            await resp.Content.CopyToAsync(fs, ct);
        }

        // TRUST ASSUMPTION (documented, see architecture.md): Forge publishes no independent
        // signatures, so the plain-text .sha1 next to each artifact comes from the SAME maven as
        // the jar — it protects against corruption/truncation, not against a compromised server.
        // But since this jar is about to be EXECUTED (java -jar ... --installServer), the check is
        // now REQUIRED rather than best-effort: no readable .sha1, no install. Maven repos always
        // publish it, so a missing one means a broken download path, not a normal condition.
        var expectedSha1 = await TryGetRemoteHashAsync(installerUrl + ".sha1", ct);
        if (string.IsNullOrEmpty(expectedSha1))
        {
            TryDelete(installerPath);
            throw new InvalidOperationException(Localizer.Get("Msg_ForgeNoChecksum"));
        }
        log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
        await DownloadVerifier.VerifyAsync(installerPath, expectedSha1, HashAlgorithmName.SHA1, ct);

        // Structural sanity net before handing the jar to java (same idea as the Fabric jar
        // validation): it must at least BE a Forge installer. A swapped or truncated file is
        // deleted and refused instead of being executed.
        log?.Report(Localizer.Get("Msg_VerifyingJarStructure"));
        ValidateForgeInstallerJar(installerPath);

        log?.Report(Localizer.Get("Msg_ForgeRunningInstaller"));
        await RunForgeInstallerAsync(installerPath, folder, javaPath, log, ct);

        // Clean up the installer and its logs.
        TryDelete(installerPath);
        TryDelete(installerPath + ".log");
        TryDelete(Path.Combine(folder, "installer.log"));

        // Modern Forge: an args file under libraries/.
        var argName = OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";
        var argsFile = Path.Combine(folder, "libraries", "net", "minecraftforge", "forge", fullId, argName);
        if (File.Exists(argsFile))
            return new ForgeInstallResult(null, fullId);

        // Old Forge (≤1.16.5): a runnable forge jar in the root.
        var jar = Directory.EnumerateFiles(folder, "forge-*.jar")
            .Select(Path.GetFileName)
            .FirstOrDefault(n => n is not null && !n.Contains("installer", StringComparison.OrdinalIgnoreCase));
        return new ForgeInstallResult(jar, null);
    }

    private static async Task RunForgeInstallerAsync(string installerPath, string folder, string javaPath,
        IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installServer");

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) log?.Report(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // Hard cap so a stuck installer can never hang the creation flow forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new InvalidOperationException(Localizer.Get("Msg_ForgeInstallerTimeout"));
        }

        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.Format(Localizer.Get("Msg_ForgeInstallerFailed"), p.ExitCode));
    }

    /// <summary>
    /// Structural validation of a downloaded Forge installer before it is executed: it must be a
    /// readable jar that actually looks like a Forge installer — carrying the install_profile.json
    /// every Forge installer embeds, or (fallback for exotic/legacy layouts) a manifest whose
    /// Main-Class names the installer. On failure the file is deleted and an exception is thrown,
    /// so a swapped or truncated download never reaches <c>java -jar</c>.
    /// </summary>
    public static void ValidateForgeInstallerJar(string jarPath)
    {
        var looksLikeInstaller = false;
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            looksLikeInstaller = zip.GetEntry("install_profile.json") is not null || HasInstallerMainClass(zip);
        }
        catch
        {
            // Not a readable jar at all: stays false and is rejected below.
        }

        if (!looksLikeInstaller)
        {
            TryDelete(jarPath);
            throw new InvalidOperationException(Localizer.Get("Msg_ForgeInstallerInvalid"));
        }
    }

    private static bool HasInstallerMainClass(ZipArchive zip)
    {
        var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
        if (manifest is null) return false;
        using var sr = new StreamReader(manifest.Open());
        var text = sr.ReadToEnd();
        return text.Contains("Main-Class", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("installer", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Fetches a plain-text checksum file (e.g. Maven's "*.sha1"). Returns null if it's missing or
    /// the request fails — the Forge install path treats that as a refusal (the installer is about
    /// to be executed, so its checksum is mandatory).
    /// </summary>
    private static async Task<string?> TryGetRemoteHashAsync(string url, CancellationToken ct)
    {
        try
        {
            var text = await Http.GetStringAsync(url, ct);
            return text.Trim();
        }
        catch
        {
            return null;
        }
    }
}
