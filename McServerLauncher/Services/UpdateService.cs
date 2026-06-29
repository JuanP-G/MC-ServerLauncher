using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>
/// Comprueba en las Releases de GitHub si hay una versión más nueva que la instalada.
/// </summary>
public class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/JuanP-G/MC-ServerLauncher/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Datos de la actualización. <see cref="InstallerUrl"/> es el .exe del instalador (null si no hay).</summary>
    public record UpdateInfo(string Version, string Url, string? InstallerUrl);

    /// <summary>Devuelve la última versión si es más nueva que <paramref name="current"/>; si no, null.</summary>
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

        // Buscar el instalador (.exe) entre los assets de la release para poder actualizar desde la app.
        string? installerUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("browser_download_url", out var d))
                {
                    installerUrl = d.GetString();
                    break;
                }
            }
        }

        return new UpdateInfo(tag.TrimStart('v', 'V'), url, installerUrl);
    }

    /// <summary>Descarga el instalador a <paramref name="destPath"/>. Devuelve la ruta descargada.</summary>
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
