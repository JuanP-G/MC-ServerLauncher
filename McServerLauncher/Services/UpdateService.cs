using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Checks the GitHub Releases for a version newer than the installed one.
/// </summary>
public class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/JuanP-G/MC-ServerLauncher/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Update data. <see cref="InstallerUrl"/>/<see cref="InstallerName"/> are the installer .exe
    /// (null if there isn't one). <see cref="Sha256SumsUrl"/> is a "SHA256SUMS.txt" asset published
    /// alongside it (null on releases published before this existed), used to verify the installer
    /// before running it.
    /// </summary>
    public record UpdateInfo(string Version, string Url, string? InstallerUrl, string? InstallerName, string? Sha256SumsUrl);

    /// <summary>Returns the latest version if it is newer than <paramref name="current"/>; otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        req.Headers.UserAgent.ParseAdd("MC-ServerLauncher");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (tag is null || url is null) return null;

        var latest = ParseVersion(tag);
        if (latest is null) return null;

        var cur = Normalize(current);
        if (latest <= cur) return null;

        // Look for the installer (.exe) and its checksum file among the release assets.
        string? installerUrl = null;
        string? installerName = null;
        string? sha256SumsUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var downloadUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (name is null || downloadUrl is null) continue;

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    installerUrl = downloadUrl;
                    installerName = name;
                }
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sha256SumsUrl = downloadUrl;
                }
            }
        }

        return new UpdateInfo(tag.TrimStart('v', 'V'), url, installerUrl, installerName, sha256SumsUrl);
    }

    /// <summary>
    /// Reads the expected checksum for <paramref name="fileName"/> from a "SHA256SUMS.txt"-style
    /// asset (lines of "&lt;hex&gt;  &lt;filename&gt;", one per file). Returns null if the asset is
    /// unreachable, malformed, or has no entry for that file — the in-app updater treats that as a
    /// refusal to run the installer (verification is mandatory), falling back to the release page.
    /// </summary>
    public async Task<string?> GetExpectedSha256Async(string sha256SumsUrl, string fileName, CancellationToken ct = default)
    {
        try
        {
            var text = await Http.GetStringAsync(sha256SumsUrl, ct);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var hash = parts[0];
                // sha256sum-style output prefixes the filename with '*' in binary mode.
                var name = parts[1].TrimStart('*');
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return hash;
            }
        }
        catch
        {
            // Best-effort: an unreachable or malformed sums file just means no verification.
        }
        return null;
    }

    /// <summary>Downloads the installer to <paramref name="destPath"/>. Returns the downloaded path.</summary>
    public async Task<string> DownloadInstallerAsync(string url, string destPath, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("MC-ServerLauncher");

        using var resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using (var fs = File.Create(destPath))
        await using (var s = await resp.Content.ReadAsStreamAsync(ct))
            await s.CopyToAsync(fs, ct);

        return destPath;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        var parts = s.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        var build = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
        return new Version(major, minor, build);
    }
}
