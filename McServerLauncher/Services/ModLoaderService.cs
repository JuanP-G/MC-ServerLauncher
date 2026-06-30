using System.IO;
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
}
