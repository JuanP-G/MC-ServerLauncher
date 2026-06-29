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
/// Detects the machine's Java installations and, if needed, downloads the right version
/// (Adoptium Temurin) for a specific Minecraft version.
/// </summary>
public partial class JavaService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public record JavaInstall(string Path, int Major);

    private static string ManagedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "java");

    [GeneratedRegex("version \"(\\d+)(?:\\.(\\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>Searches for java.exe in common locations and returns their versions.</summary>
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
            catch { /* ignore */ }
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

    /// <summary>Runs "java -version" and returns the major version (8, 17, 21, 25...). 0 on failure.</summary>
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

    /// <summary>An installed Java version is valid for the required one (exact, or newer if 17+).</summary>
    public static bool IsCompatible(int installed, int required)
        => installed == required || (required >= 17 && installed >= required);

    /// <summary>
    /// Reads the Java a server.jar needs (modern versions include it in version.json).
    /// Returns null if it can't be determined (very old servers or non-standard jars).
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
    /// Returns the path to a java.exe compatible with the required version. If none is
    /// installed, downloads and installs the matching Temurin. Throws if it can't.
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

        // Already installed by us before?
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
            throw new InvalidOperationException($"No Java {major} download was found for Windows.");

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
        try { File.Delete(zipPath); } catch { /* doesn't matter */ }

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
