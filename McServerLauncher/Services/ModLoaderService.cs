using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

public class ModLoaderService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

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

        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
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
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.Format(Localizer.Get("Msg_ForgeInstallerFailed"), p.ExitCode));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
