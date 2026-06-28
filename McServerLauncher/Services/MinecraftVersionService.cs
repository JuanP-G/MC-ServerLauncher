using System.IO;
using System.Net.Http;
using System.Text.Json;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Obtiene la lista de versiones de Minecraft del manifiesto oficial de Mojang y
/// descarga el server.jar de la versión elegida.
/// </summary>
public class MinecraftVersionService
{
    private const string ManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Descarga el manifiesto y devuelve (versión release más reciente, lista de versiones).</summary>
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

    /// <summary>URL del server.jar y versión de Java requerida para una versión de Minecraft.</summary>
    public record VersionDetails(string ServerUrl, int JavaMajor);

    /// <summary>Resuelve la URL del server.jar para una versión concreta.</summary>
    public async Task<string> GetServerJarUrlAsync(MinecraftVersion version, CancellationToken ct = default)
        => (await GetVersionDetailsAsync(version, ct)).ServerUrl;

    /// <summary>Resuelve la descarga del servidor y la versión de Java que necesita esa versión.</summary>
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
                $"La versión {version.Id} no tiene un servidor descargable disponible.");
        }

        // Java recomendado por Mojang (si falta, asumimos Java 8 para versiones muy antiguas).
        var javaMajor = 8;
        if (root.TryGetProperty("javaVersion", out var jv) &&
            jv.TryGetProperty("majorVersion", out var mj) && mj.TryGetInt32(out var m))
        {
            javaMajor = m;
        }

        return new VersionDetails(serverUrl, javaMajor);
    }

    /// <summary>Descarga un archivo a disco.</summary>
    public async Task DownloadFileAsync(string url, string destPath, IProgress<string>? log,
        CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        log?.Report(totalMb > 0
            ? $"Descargando server.jar ({totalMb:0.#} MB)..."
            : "Descargando server.jar...");

        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);

        log?.Report("Descarga completada.");
    }
}
