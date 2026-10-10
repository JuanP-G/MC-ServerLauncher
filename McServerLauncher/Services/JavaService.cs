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

    /// <summary>A Java runtime found on the machine: where its executable is, and its major version.</summary>
    /// <param name="Path">Full path to the <c>java</c> executable, which is what a server is launched with.</param>
    /// <param name="Major">The feature release (8, 17, 21…) — the only part a Minecraft version cares about.</param>
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

    /// <summary>What each Java executable answered, keyed by its path and stamped with its file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, int Major)>
        Versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The major version (8, 17, 21, 25...) of a Java executable. 0 on failure.</summary>
    /// <remarks>
    /// <para>
    /// Asking means starting a JVM, a fraction of a second to more than one on a slow disk, and it
    /// was asked twice on every start — once to check the Java and again to build the command line —
    /// on the UI thread, and once per JDK found when a server was created. The answer only changes
    /// when the file does, so it is remembered against the file's size and write time; a Java
    /// updated in place is asked again.
    /// </para>
    /// <para>
    /// A bare "java" resolved through the PATH has no file to stamp and is not remembered.
    /// </para>
    /// </remarks>
    public int GetMajorVersion(string javaExe)
    {
        FileInfo? file = null;
        try { file = File.Exists(javaExe) ? new FileInfo(javaExe) : null; }
        catch { /* an unusable path: asked, not remembered */ }

        if (file is not null && Versions.TryGetValue(file.FullName, out var known)
            && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
            return known.Major;

        var major = AskMajorVersion(javaExe);
        if (file is not null && major > 0)
            Versions[file.FullName] = (file.Length, file.LastWriteTimeUtc, major);
        return major;
    }

    /// <summary>Runs "java -version" and reads the major version out of what it prints.</summary>
    private static int AskMajorVersion(string javaExe)
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

            // Both streams at once, then a deadline that actually applies: reading one to the end
            // before the other could wait forever on a process blocked writing the second, and
            // WaitForExit after ReadToEnd never got the chance to time anything out.
            var error = p.StandardError.ReadToEndAsync();
            var standard = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return 0;
            }
            var output = error.GetAwaiter().GetResult() + standard.GetAwaiter().GetResult();

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
    /// <remarks>
    /// One exception, for Java 16 — what Mojang declares for Minecraft 1.17 and 1.17.1. Adoptium
    /// publishes no Java 16 at all, so if 17 did not count, a 1.17 server could neither use the Java
    /// 17 already on the machine nor download a 16, and would not start without a Java installed by
    /// hand. 17 runs those versions; anything later is not promised to.
    /// </remarks>
    public static bool IsCompatible(int installed, int required)
        => installed == required
           || (required == 16 && installed == 17)
           || (required >= 17 && installed >= required);

    /// <summary>The Java to download when nothing installed fits <paramref name="required"/>.</summary>
    /// <remarks>Java 16 does not exist on Adoptium; 17 is the one that runs what asks for it.</remarks>
    internal static int DownloadableMajor(int required) => required == 16 ? 17 : required;

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
        // Off the caller's thread: it starts a JVM per installation found, and the caller is the UI.
        var installed = await Task.Run(DetectInstalled, ct);
        var match = installed.FirstOrDefault(i => IsCompatible(i.Major, requiredMajor));
        if (match is not null)
        {
            log?.Report(string.Format(Localizer.Get("Msg_JavaCompatibleFound"), match.Major));
            return match.Path;
        }

        var download = DownloadableMajor(requiredMajor);
        log?.Report(string.Format(Localizer.Get("Msg_JavaNotCompatibleDownloading"), download));
        return await DownloadAdoptiumAsync(download, log, ct);
    }

    /// <summary>The Adoptium architectures to ask for, in order, on a machine of <paramref name="arch"/>.</summary>
    /// <remarks>
    /// <para>
    /// ARM machines get a native build where there is one, and an x64 one where there is not — but
    /// only where the system can run it. Adoptium has no ARM JRE for Java 8 on macOS, nor for 8,
    /// 16 or 17 on Windows (checked against its API), so asking for aarch64 alone left every server
    /// older than 1.20.5 on Windows ARM, and older than 1.17 on Apple Silicon, without a Java. Both
    /// systems run x64 code (Windows 11 by emulation, macOS through Rosetta); Linux does not, so an
    /// ARM Linux machine only ever gets an ARM build.
    /// </para>
    /// <para>
    /// The OS architecture, not the process's: an x64 build of this app on a Windows ARM machine
    /// still wants a native Java if one exists.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> AdoptiumArchitectures(Architecture arch, bool emulatesX64) => arch switch
    {
        Architecture.Arm64 => emulatesX64 ? new[] { "aarch64", "x64" } : new[] { "aarch64" },
        Architecture.X86 => new[] { "x86" },
        _ => new[] { "x64" }
    };

    /// <summary>The download link and checksum of the first package in an Adoptium answer.</summary>
    private static (string? Link, string? Checksum) FirstPackage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var asset in doc.RootElement.EnumerateArray())
        {
            if (asset.TryGetProperty("binary", out var b) &&
                b.TryGetProperty("package", out var pkg) &&
                pkg.TryGetProperty("link", out var lk))
            {
                return (lk.GetString(), pkg.TryGetProperty("checksum", out var cs) ? cs.GetString() : null);
            }
        }
        return (null, null);
    }

    private async Task<string> DownloadAdoptiumAsync(int major, IProgress<string>? log, CancellationToken ct)
    {
        var target = Path.Combine(ManagedRoot, $"jre-{major}");

        // Already installed by us before?
        var existing = FindJavaExe(target);
        if (existing is not null) return existing;

        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        var archs = AdoptiumArchitectures(RuntimeInformation.OSArchitecture,
            emulatesX64: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

        string? link = null;
        string? checksum = null;
        foreach (var arch in archs)
        {
            var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                         $"?architecture={arch}&image_type=jre&os={os}&vendor=eclipse";
            (link, checksum) = FirstPackage(await Http.GetStringAsync(apiUrl, ct));
            if (!string.IsNullOrEmpty(link)) break;
        }
        if (string.IsNullOrEmpty(link))
            throw new InvalidOperationException(
                $"No Java {major} download was found for {os}/{string.Join(" or ", archs)}.");

        Directory.CreateDirectory(ManagedRoot);
        var isZip = OperatingSystem.IsWindows();
        var archivePath = Path.Combine(ManagedRoot, $"jre-{major}" + (isZip ? ".zip" : ".tar.gz"));

        // No checksum, no Java. This archive is extracted and then EXECUTED to run the user's
        // servers, so verifying it cannot be best-effort the way it was: a response without the
        // field meant downloading, unpacking and running a JRE that nothing had checked. Adoptium
        // publishes a checksum for every binary, so a missing one is a broken download path, not a
        // normal condition — the same rule the Forge installer already follows.
        if (string.IsNullOrEmpty(checksum))
            throw new InvalidOperationException(Localizer.Get("Msg_JavaNoChecksum"));

        log?.Report(Localizer.Get("Msg_JavaDownloading"));
        using (var resp = await Http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await AtomicDownload.ToFileAsync(resp.Content, archivePath,
                verifyAsync: async (part, token) =>
                {
                    log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
                    await DownloadVerifier.VerifyAsync(part, checksum, HashAlgorithmName.SHA256, token);
                },
                ct: ct);
        }

        log?.Report(Localizer.Get("Msg_JavaInstalling"));
        // Unpacked beside the target and moved into place whole. Unpacking straight into it left a
        // half-extracted runtime after an interruption, and since an existing jre-N folder with a
        // java in it counts as installed (above), that broken runtime was used from then on.
        var partial = target + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        Directory.CreateDirectory(partial);
        if (isZip)
            ZipFile.ExtractToDirectory(archivePath, partial);
        else
            await ExtractTarGzAsync(archivePath, partial, ct);

        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(partial, target);
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
