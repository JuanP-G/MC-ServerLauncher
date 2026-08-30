using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Checks the GitHub Releases for a version newer than the installed one.
/// </summary>
public class UpdateService
{
    /// <summary>
    /// The release list, not <c>/releases/latest</c>.
    /// </summary>
    /// <remarks>
    /// GitHub leaves pre-releases out of <c>/releases/latest</c> entirely, so a beta published that
    /// way is invisible to the app and could only ever be installed by hand. Reading the list keeps
    /// betas reachable, at the price of having to pick the newest one here rather than being handed
    /// it — and of having to say clearly, everywhere it is offered, that it is a beta.
    /// </remarks>
    private const string ApiUrl = "https://api.github.com/repos/JuanP-G/MC-ServerLauncher/releases?per_page=20";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Update data. <see cref="PackageUrl"/>/<see cref="PackageName"/> are the package for the
    /// platform this app is running on — the Windows installer, the Linux AppImage or the macOS
    /// .dmg for this architecture — and are null when the release ships nothing usable here.
    /// <see cref="ChecksumUrl"/> is the asset that carries its SHA-256, used to verify the download
    /// before it is installed; null on releases published before that existed.
    /// </summary>
    /// <remarks>
    /// <c>IsPreRelease</c> exists so nobody can be moved onto a beta without being told: it is what
    /// the banner and the notification use to say so, before the button is pressed rather than
    /// after.
    /// </remarks>
    public record UpdateInfo(string Version, string Url, string? PackageUrl, string? PackageName,
        string? ChecksumUrl, bool IsPreRelease = false);

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
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var root = PickNewestRelease(doc.RootElement, current);
        if (root is not { } release) return null;

        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = release.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (tag is null || url is null) return null;

        var isPreRelease = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;

        var assets = ReadAssets(release);
        var (packageName, packageUrl) = PickPackage(assets, CurrentOs, RuntimeInformation.OSArchitecture);

        // Windows keeps using the shared SHA256SUMS.txt that publish.ps1 writes. The AppImage and
        // the .dmg files are built afterwards, by three workflows running at the same time, so each
        // publishes its own "<package>.sha256" rather than racing to append to one shared file.
        string? checksumUrl = null;
        if (packageName is not null)
        {
            if (packageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                assets.TryGetValue("SHA256SUMS.txt", out checksumUrl);
            else
                assets.TryGetValue(packageName + ".sha256", out checksumUrl);
        }

        return new UpdateInfo(tag.TrimStart('v', 'V'), url, packageUrl, packageName, checksumUrl, isPreRelease);
    }

    /// <summary>
    /// The newest published release above <paramref name="current"/>, betas included, or null.
    /// </summary>
    /// <remarks>
    /// Drafts are skipped: they are not published and their assets may not exist yet. Order comes
    /// from the version numbers rather than from the list, because GitHub sorts by creation date
    /// and a patch to an older line can be published after a newer release.
    /// </remarks>
    private static JsonElement? PickNewestRelease(JsonElement releases, Version current)
    {
        // Normalized here rather than trusted from the caller: .NET reports an unspecified
        // component as -1, so an un-padded 1.10.1 compares as 1.10.1.-1 and every parsed tag looks
        // newer than it — the app would offer you the version you are already running.
        current = Normalize(current);

        JsonElement? best = null;
        Version? bestVersion = null;

        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;

            var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || ParseVersion(tag) is not { } version) continue;
            if (version <= current) continue;
            if (bestVersion is not null && version <= bestVersion) continue;

            best = release;
            bestVersion = version;
        }
        return best;
    }

    private static Dictionary<string, string> ReadAssets(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var downloadUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
            if (name is not null && downloadUrl is not null) map[name] = downloadUrl;
        }
        return map;
    }

    internal static string CurrentOs =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos" : "other";

    /// <summary>
    /// The asset that updates <em>this</em> install: the .exe on Windows, the AppImage on Linux,
    /// and the .dmg matching the running architecture on macOS.
    /// </summary>
    /// <remarks>
    /// Takes the platform as arguments rather than reading it from the environment so the choice
    /// can be checked for every platform from any of them — picking the wrong asset here would ship
    /// users an app that cannot start, and it is not something to find out only on release day.
    /// </remarks>
    internal static (string? Name, string? Url) PickPackage(
        IReadOnlyDictionary<string, string> assets, string os, Architecture arch)
    {
        var wanted = os switch
        {
            "windows" => assets.Keys.FirstOrDefault(k => k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
            "linux" => assets.Keys.FirstOrDefault(k => k.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)),
            // Installing the wrong architecture would produce an app that cannot start, so the name
            // is matched exactly rather than falling back to "any .dmg".
            "macos" => assets.Keys.FirstOrDefault(k =>
                k.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) &&
                k.Contains(arch == Architecture.Arm64 ? "AppleSilicon" : "Intel",
                           StringComparison.OrdinalIgnoreCase)),
            _ => null
        };

        return wanted is not null ? (wanted, assets[wanted]) : (null, null);
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

        // Atomic, so an interrupted download can't leave a plausible-looking half installer where
        // the real one should be (the caller verifies it against SHA256SUMS.txt afterwards).
        await AtomicDownload.ToFileAsync(resp.Content, destPath, ct: ct);

        return destPath;
    }

    /// <summary>
    /// Pads a version out to four numbers, so comparisons never depend on how many were written.
    /// </summary>
    /// <remarks>
    /// The fourth number is the beta counter, and it extends the stable a beta <em>follows</em>:
    /// 1.10.3, then 1.10.3.1 and 1.10.3.2, with the finished work shipping as the next stable
    /// number. Numbering betas after the version they lead to would make a stable sort below its
    /// own betas and strand everyone who tested them. .NET reports an unspecified component as -1,
    /// so padding is what stops 1.10.3 comparing as 1.10.3.-1 and losing to itself.
    /// </remarks>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        var parts = s.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return null;
        var build = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
        var revision = parts.Length > 3 && int.TryParse(parts[3], out var r) ? r : 0;
        return new Version(major, minor, build, revision);
    }
}
