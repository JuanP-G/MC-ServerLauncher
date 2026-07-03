using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using McServerLauncher.Localization;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Gets the list of Minecraft versions from Mojang's official manifest and
/// downloads the server.jar of the chosen version.
/// </summary>
public class MinecraftVersionService
{
    private const string ManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Downloads the manifest and returns (latest release version, list of versions).</summary>
    public async Task<(string LatestRelease, List<MinecraftVersion> Versions)> GetVersionsAsync(
        CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ManifestUrl, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var latest = root.GetProperty("latest").GetProperty("release").GetString() ?? string.Empty;

        var list = new List<MinecraftVersion>();
        foreach (var v in root.GetProperty("versions").EnumerateArray())
        {
            list.Add(new MinecraftVersion
            {
                Id = v.GetProperty("id").GetString() ?? string.Empty,
                Type = v.GetProperty("type").GetString() ?? string.Empty,
                Url = v.GetProperty("url").GetString() ?? string.Empty
            });
        }
        return (latest, list);
    }

    /// <summary>server.jar URL, required Java version and its official SHA-1 (for integrity checking).</summary>
    public record VersionDetails(string ServerUrl, int JavaMajor, string? Sha1);

    /// <summary>Resolves the server.jar URL for a specific version.</summary>
    public async Task<string> GetServerJarUrlAsync(MinecraftVersion version, CancellationToken ct = default)
        => (await GetVersionDetailsAsync(version, ct)).ServerUrl;

    /// <summary>Resolves the server download and the Java version that version needs.</summary>
    public async Task<VersionDetails> GetVersionDetailsAsync(MinecraftVersion version, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(version.Url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server", out var server) ||
            !server.TryGetProperty("url", out var url) ||
            url.GetString() is not { Length: > 0 } serverUrl)
        {
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_NoServerDownload"), version.Id));
        }

        // Java recommended by Mojang (if missing, assume Java 8 for very old versions).
        var javaMajor = 8;
        if (root.TryGetProperty("javaVersion", out var jv) &&
            jv.TryGetProperty("majorVersion", out var mj) && mj.TryGetInt32(out var m))
        {
            javaMajor = m;
        }

        var sha1 = server.TryGetProperty("sha1", out var shaEl) ? shaEl.GetString() : null;

        return new VersionDetails(serverUrl, javaMajor, sha1);
    }

    /// <summary>
    /// Downloads a file to disk. If <paramref name="expectedSha1"/> is given (Mojang returns one for
    /// every server.jar), the download is verified against it; a mismatch deletes the file and throws.
    /// </summary>
    public async Task DownloadFileAsync(string url, string destPath, IProgress<string>? log,
        string? expectedSha1 = null, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        log?.Report(totalMb > 0
            ? string.Format(Localizer.Get("Msg_DownloadingJarSize"), totalMb.ToString("0.#"))
            : Localizer.Get("Msg_DownloadingJar"));

        await using (var fs = File.Create(destPath))
            await resp.Content.CopyToAsync(fs, ct);

        if (!string.IsNullOrEmpty(expectedSha1))
        {
            log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
            await DownloadVerifier.VerifyAsync(destPath, expectedSha1, HashAlgorithmName.SHA1, ct);
        }

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
    }
}
