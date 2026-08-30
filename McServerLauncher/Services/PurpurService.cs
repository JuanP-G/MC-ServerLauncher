using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Downloads the Purpur server jar. Purpur is a Paper fork: a runnable jar, plugins in
/// <c>plugins/</c>, and every Bukkit plugin works on it unchanged.
/// </summary>
public class PurpurService
{
    private const string ApiBase = "https://api.purpurmc.org/v2/purpur";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static PurpurService()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "JuanP-G/MC-ServerLauncher");
    }

    /// <summary>A build of Purpur, and the hash their API publishes for it.</summary>
    /// <remarks>
    /// The hash is MD5 because it is the only checksum Purpur publishes — Paper gives SHA-256 and
    /// this one does not. It is not a downgrade waved through: the download comes over HTTPS from
    /// Purpur's own API, which is what authenticates it, and the hash is here to catch a truncated
    /// or corrupted file. MD5 is unfit for proving a file was not <em>substituted</em>, and nothing
    /// here relies on it for that.
    /// </remarks>
    public record PurpurBuild(string Version, string Build, string FileName, string Url, string? Md5);

    /// <summary>Latest build for a Minecraft version, or null when Purpur has none for it.</summary>
    public async Task<PurpurBuild?> GetLatestBuildAsync(string mcVersion, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"{ApiBase}/{Uri.EscapeDataString(mcVersion)}/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;   // Purpur 404s versions it never built

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        // A build that did not compile is listed like any other; installing it gives a server that
        // cannot start, with nothing on screen to say why.
        if (root.TryGetProperty("result", out var result) &&
            !string.Equals(result.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            return null;

        var build = root.TryGetProperty("build", out var b) ? b.GetString() : null;
        if (string.IsNullOrEmpty(build)) return null;

        var md5 = root.TryGetProperty("md5", out var h) ? h.GetString() : null;

        return new PurpurBuild(
            mcVersion, build!, $"purpur-{mcVersion}-{build}.jar",
            $"{ApiBase}/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(build!)}/download", md5);
    }

    /// <summary>Downloads a build to <paramref name="destPath"/>, verified before it is kept.</summary>
    public async Task DownloadPurpurServerAsync(PurpurBuild build, string destPath, IProgress<string>? log,
        CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        log?.Report(totalMb > 0
            ? string.Format(Localizer.Get("Msg_DownloadingJarSize"), totalMb.ToString("0.#"))
            : Localizer.Get("Msg_DownloadingJar"));

        await AtomicDownload.ToFileAsync(resp.Content, destPath,
            verifyAsync: async (part, token) =>
            {
                if (string.IsNullOrEmpty(build.Md5)) return;
                log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
                await DownloadVerifier.VerifyAsync(part, build.Md5, HashAlgorithmName.MD5, token);
            },
            ct: ct);

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
    }
}
