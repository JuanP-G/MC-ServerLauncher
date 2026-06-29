using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Detecta las instalaciones de Java del equipo y, si hace falta, descarga la versión adecuada
/// (Temurin de Adoptium) para una versión de Minecraft concreta.
/// </summary>
public partial class JavaService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public record JavaInstall(string Path, int Major);

    private static string ManagedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "java");

    [GeneratedRegex("version \"(\\d+)(?:\\.(\\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>Busca java.exe en ubicaciones habituales y devuelve sus versiones.</summary>
    public List<JavaInstall> DetectInstalled()
    {
        var candidates = new List<string>();

        void AddFrom(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var exe = Path.Combine(dir, "bin", "java.exe");
                    if (File.Exists(exe)) candidates.Add(exe);
                }
            }
            catch { /* ignorar */ }
        }

        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            AddFrom(Path.Combine(pf, "Eclipse Adoptium"));
            AddFrom(Path.Combine(pf, "Java"));
            AddFrom(Path.Combine(pf, "Microsoft"));
            AddFrom(Path.Combine(pf, "Zulu"));
            AddFrom(Path.Combine(pf, "Amazon Corretto"));
        }
        AddFrom(ManagedRoot);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(exe)) candidates.Add(exe);
        }

        var result = new List<JavaInstall>();
        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var major = GetMajorVersion(exe);
            if (major > 0) result.Add(new JavaInstall(exe, major));
        }
        return result;
    }

    /// <summary>Ejecuta "java -version" y devuelve la versión mayor (8, 17, 21, 25...). 0 si falla.</summary>
    public int GetMajorVersion(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var m = VersionRegex().Match(output);
            if (!m.Success) return 0;
            var first = int.Parse(m.Groups[1].Value);
            // "1.8" => Java 8
            if (first == 1 && m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
            return first;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Una versión de Java instalada vale para la requerida (exacta, o más nueva si es 17+).</summary>
    public static bool IsCompatible(int installed, int required)
        => installed == required || (required >= 17 && installed >= required);

    /// <summary>
    /// Lee el Java que necesita un server.jar (las versiones modernas lo incluyen en version.json).
    /// Devuelve null si no se puede determinar (servidores muy antiguos o jars no estándar).
    /// </summary>
    public int? GetRequiredJavaFromJar(string jarPath)
    {
        try
        {
            if (!File.Exists(jarPath)) return null;
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("version.json");
            if (entry is null) return null;

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("java_version", out var jv) && jv.TryGetInt32(out var m))
                return m;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Devuelve la ruta a un java.exe compatible con la versión requerida. Si no hay ninguno
    /// instalado, descarga e instala el Temurin correspondiente. Lanza si no se puede.
    /// </summary>
    public async Task<string> EnsureJavaAsync(int requiredMajor, IProgress<string>? log, CancellationToken ct = default)
    {
        var match = DetectInstalled().FirstOrDefault(i => IsCompatible(i.Major, requiredMajor));
        if (match is not null)
        {
            log?.Report(string.Format(Localizer.Get("Msg_JavaCompatibleFound"), match.Major));
            return match.Path;
        }

        log?.Report(string.Format(Localizer.Get("Msg_JavaNotCompatibleDownloading"), requiredMajor));
        return await DownloadAdoptiumAsync(requiredMajor, log, ct);
    }

    private async Task<string> DownloadAdoptiumAsync(int major, IProgress<string>? log, CancellationToken ct)
    {
        var target = Path.Combine(ManagedRoot, $"jre-{major}");

        // ¿Ya instalado por nosotros antes?
        var existing = FindJavaExe(target);
        if (existing is not null) return existing;

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "x86",
            _ => "x64"
        };
        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                     $"?architecture={arch}&image_type=jre&os=windows&vendor=eclipse";
        var json = await Http.GetStringAsync(apiUrl, ct);

        string? link = null;
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var asset in doc.RootElement.EnumerateArray())
            {
                if (asset.TryGetProperty("binary", out var b) &&
                    b.TryGetProperty("package", out var pkg) &&
                    pkg.TryGetProperty("link", out var lk))
                {
                    link = lk.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(link))
            throw new InvalidOperationException($"No se encontró una descarga de Java {major} para Windows.");

        Directory.CreateDirectory(ManagedRoot);
        var zipPath = Path.Combine(ManagedRoot, $"jre-{major}.zip");

        log?.Report(Localizer.Get("Msg_JavaDownloading"));
        using (var resp = await Http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct);
        }

        log?.Report(Localizer.Get("Msg_JavaInstalling"));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ZipFile.ExtractToDirectory(zipPath, target);
        try { File.Delete(zipPath); } catch { /* da igual */ }

        var javaExe = FindJavaExe(target)
            ?? throw new InvalidOperationException(Localizer.Get("Msg_JavaExeNotFound"));
        log?.Report(string.Format(Localizer.Get("Msg_JavaInstalled"), major));
        return javaExe;
    }

    private static string? FindJavaExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.GetFiles(root, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.EndsWith(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
