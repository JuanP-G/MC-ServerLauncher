using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Downloads the Paper server jar from PaperMC's "fill" API (v3). Paper is a runnable jar, so it
/// launches like vanilla (-jar). Plugins go in the server's plugins/ folder.
/// </summary>
public class PaperService
{
    private const string ApiBase = "https://fill.papermc.io/v3/projects/paper";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static PaperService()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "JuanP-G/MC-ServerLauncher");
    }

    public record PaperBuild(int Build, string FileName, string Url, string? Sha256);

    /// <summary>
    /// Latest build for a Minecraft version. Prefers the newest STABLE build; if none exist yet
    /// (very new versions only have ALPHA/BETA), falls back to the newest build. Null if unavailable.
    /// </summary>
    public async Task<PaperBuild?> GetLatestBuildAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync($"{ApiBase}/versions/{mcVersion}/builds", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return null;

        // The array is newest-first. Prefer the first STABLE build, else the newest overall.
        JsonElement chosen = root[0];
        foreach (var b in root.EnumerateArray())
        {
            if (b.TryGetProperty("channel", out var ch) && ch.GetString() == "STABLE")
            {
                chosen = b;
                break;
            }
        }

        if (!chosen.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server:default", out var server))
            return null;

        var build = chosen.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) ? id : 0;
        var name = server.TryGetProperty("name", out var n) ? n.GetString() : null;
        var url = server.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            return null;

        string? sha256 = null;
        if (server.TryGetProperty("checksums", out var checksums) && checksums.TryGetProperty("sha256", out var s))
            sha256 = s.GetString();

        return new PaperBuild(build, name, url, sha256);
    }

    public async Task DownloadPaperServerAsync(PaperBuild build, string destPath, IProgress<string>? log,
        CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        log?.Report(totalMb > 0
            ? string.Format(Localizer.Get("Msg_DownloadingJarSize"), totalMb.ToString("0.#"))
            : Localizer.Get("Msg_DownloadingJar"));

        await using (var fs = File.Create(destPath))
            await resp.Content.CopyToAsync(fs, ct);

        if (!string.IsNullOrEmpty(build.Sha256))
        {
            log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
            await DownloadVerifier.VerifyAsync(destPath, build.Sha256, HashAlgorithmName.SHA256, ct);
        }

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
    }
}
