using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Detects the machine's Java installations and, if needed, downloads the right version
/// (Adoptium Temurin) for a specific Minecraft version. Works on Windows and Linux.
/// </summary>
public partial class JavaService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public record JavaInstall(string Path, int Major);

    private static string ManagedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher", "java");

    /// <summary>The java executable name for the current OS ("java.exe" on Windows, "java" elsewhere).</summary>
    private static string JavaExeName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    [GeneratedRegex("version \"(\\d+)(?:\\.(\\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>Searches for the java executable in common locations and returns their versions.</summary>
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
                    var exe = Path.Combine(dir, "bin", JavaExeName);
                    if (File.Exists(exe)) candidates.Add(exe);
                }
            }
            catch { /* ignore */ }
        }

        if (OperatingSystem.IsWindows())
        {
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
        }
        else
        {
            // Common JVM locations on Linux.
            AddFrom("/usr/lib/jvm");
            AddFrom("/usr/java");
            AddFrom("/opt/java");
            AddFrom("/opt");

            if (OperatingSystem.IsMacOS())
            {
                // macOS JDK bundles keep the JRE under <bundle>/Contents/Home.
                void AddMacFrom(string root)
                {
                    try
                    {
                        if (!Directory.Exists(root)) return;
                        foreach (var dir in Directory.GetDirectories(root))
                        {
                            var exe = Path.Combine(dir, "Contents", "Home", "bin", "java");
                            if (File.Exists(exe)) candidates.Add(exe);
                        }
                    }
                    catch { /* ignore */ }
                }

                AddMacFrom("/Library/Java/JavaVirtualMachines");
                AddMacFrom(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Java", "JavaVirtualMachines"));
            }

            var onPath = WhichJava();
            if (onPath is not null) candidates.Add(onPath);
        }
        AddFrom(ManagedRoot);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Path.Combine(javaHome, "bin", JavaExeName);
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
    /// Reads the required Java for a modern Forge server, which has no runnable server.jar of its
    /// own (it launches through an @args-file, so <see cref="GetRequiredJavaFromJar"/> on the
    /// configured jar path always fails): the Forge installer keeps the vanilla server jar under
    /// "libraries/net/minecraft/server/&lt;version&gt;/", and that jar carries the usual
    /// version.json. Prefers the folder matching <paramref name="gameVersion"/> (a leftover from a
    /// previous Minecraft version could linger after an upgrade), then tries any other. Returns
    /// null if no readable jar is found.
    /// </summary>
    public int? GetRequiredJavaFromForgeLibraries(string serverFolder, string? gameVersion)
    {
        try
        {
            var root = Path.Combine(serverFolder, "libraries", "net", "minecraft", "server");
            if (!Directory.Exists(root)) return null;

            var dirs = Directory.GetDirectories(root)
                .OrderByDescending(d => string.Equals(Path.GetFileName(d), gameVersion, StringComparison.OrdinalIgnoreCase));

            // A version dir holds several jars (server-x.y.z.jar, -extra, -srg...): the plain one
            // and -extra both carry version.json; the others just return null harmlessly.
            foreach (var dir in dirs)
            foreach (var jar in Directory.GetFiles(dir, "server-*.jar"))
            {
                if (GetRequiredJavaFromJar(jar) is { } major)
                    return major;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the Minecraft version a server.jar belongs to (modern vanilla jars include it in
    /// version.json as "id"). Returns null if it can't be determined.
    /// </summary>
    public string? GetGameVersionFromJar(string jarPath)
    {
        try
        {
            if (!File.Exists(jarPath)) return null;
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("version.json");
            if (entry is null) return null;

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } v)
                return v;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the path to a java executable compatible with the required version. If none is
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
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                     $"?architecture={arch}&image_type=jre&os={os}&vendor=eclipse";
        var json = await Http.GetStringAsync(apiUrl, ct);

        string? link = null;
        string? checksum = null;
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var asset in doc.RootElement.EnumerateArray())
            {
                if (asset.TryGetProperty("binary", out var b) &&
                    b.TryGetProperty("package", out var pkg) &&
                    pkg.TryGetProperty("link", out var lk))
                {
                    link = lk.GetString();
                    checksum = pkg.TryGetProperty("checksum", out var cs) ? cs.GetString() : null;
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(link))
            throw new InvalidOperationException($"No Java {major} download was found for {os}/{arch}.");

        Directory.CreateDirectory(ManagedRoot);
        var isZip = OperatingSystem.IsWindows();
        var archivePath = Path.Combine(ManagedRoot, $"jre-{major}" + (isZip ? ".zip" : ".tar.gz"));

        log?.Report(Localizer.Get("Msg_JavaDownloading"));
        using (var resp = await Http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(archivePath);
            await resp.Content.CopyToAsync(fs, ct);
        }

        if (!string.IsNullOrEmpty(checksum))
        {
            log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
            await DownloadVerifier.VerifyAsync(archivePath, checksum, HashAlgorithmName.SHA256, ct);
        }

        log?.Report(Localizer.Get("Msg_JavaInstalling"));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        if (isZip)
            ZipFile.ExtractToDirectory(archivePath, target);
        else
            await ExtractTarGzAsync(archivePath, target, ct);
        try { File.Delete(archivePath); } catch { /* doesn't matter */ }

        var javaExe = FindJavaExe(target)
            ?? throw new InvalidOperationException(Localizer.Get("Msg_JavaExeNotFound"));

        // Make sure the java binary is executable on Unix (tar usually preserves this, but be safe).
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(javaExe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* best-effort */ }
        }

        log?.Report(string.Format(Localizer.Get("Msg_JavaInstalled"), major));
        return javaExe;
    }

    private static async Task ExtractTarGzAsync(string targzPath, string destDir, CancellationToken ct)
    {
        await using var fs = File.OpenRead(targzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true, ct);
    }

    /// <summary>Resolves the 'java' executable on PATH (Linux/macOS) via 'which'. Null if not found.</summary>
    private static string? WhichJava()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "java",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return null;
            var outp = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            return !string.IsNullOrWhiteSpace(outp) && File.Exists(outp) ? outp : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindJavaExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            var name = JavaExeName;
            var binSuffix = Path.Combine("bin", name);
            return Directory.GetFiles(root, name, SearchOption.AllDirectories)
                .FirstOrDefault(p => p.EndsWith(binSuffix, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
