using System.Collections.Concurrent;
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
    private static readonly HttpClient Http = AppHttp.Create(TimeSpan.FromMinutes(15));

    /// <summary>
    /// A Java runtime found on the machine: where its executable is, and its major version.
    /// </summary>
    /// <param name="Path">Full path to the <c>java</c> executable, which is what a server is launched
    /// with.</param>
    /// <param name="Major">The feature release (8, 17, 21…) — the only part a Minecraft version cares
    /// about.</param>
    public record JavaInstall(string Path, int Major);

    private static string ManagedRoot => AppPaths.In("java");

    /// <summary>
    /// The java executable name for the current OS ("java.exe" on Windows, "java" elsewhere).
    /// </summary>
    private static string JavaExeName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    [GeneratedRegex("version \"(\\d+)(?:\\.(\\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>Searches for the java executable in common locations and returns their versions.</summary>
    public List<JavaInstall> DetectInstalled()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
            AddWindowsCandidates(candidates);
        else
            AddUnixCandidates(candidates);

        AddJdksIn(ManagedRoot, candidates);
        AddJavaHome(candidates);

        var result = new List<JavaInstall>();
        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var major = GetMajorVersion(exe);
            if (major > 0) result.Add(new JavaInstall(exe, major));
        }
        return result;
    }

    /// <summary>The vendors' folders under Program Files, where their installers put a JDK per version.</summary>
    private static void AddWindowsCandidates(List<string> candidates)
    {
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            AddJdksIn(Path.Combine(pf, "Eclipse Adoptium"), candidates);
            AddJdksIn(Path.Combine(pf, "Java"), candidates);
            AddJdksIn(Path.Combine(pf, "Microsoft"), candidates);
            AddJdksIn(Path.Combine(pf, "Zulu"), candidates);
            AddJdksIn(Path.Combine(pf, "Amazon Corretto"), candidates);
        }
    }

    private void AddUnixCandidates(List<string> candidates)
    {
        // Common JVM locations on Linux.
        AddJdksIn("/usr/lib/jvm", candidates);
        AddJdksIn("/usr/java", candidates);
        AddJdksIn("/opt/java", candidates);
        AddJdksIn("/opt", candidates);

        if (OperatingSystem.IsMacOS())
        {
            // macOS JDK bundles keep the JRE under <bundle>/Contents/Home.
            AddJdksIn("/Library/Java/JavaVirtualMachines", candidates, "Contents", "Home");
            AddJdksIn(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Java", "JavaVirtualMachines"), candidates, "Contents", "Home");
        }

        var onPath = WhichJava();
        if (onPath is not null) candidates.Add(onPath);
    }

    /// <summary>
    /// Adds the Java of each JDK folder under <paramref name="root"/>; <paramref name="inside"/> is the
    /// path from a JDK's folder to the one that holds its <c>bin</c> (none, except in a macOS bundle).
    /// </summary>
    private static void AddJdksIn(string root, List<string> candidates, params string[] inside)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var exe = Path.Combine([dir, .. inside, "bin", JavaExeName]);
                if (File.Exists(exe)) candidates.Add(exe);
            }
        }
        catch { /* ignore */ }
    }

    private static void AddJavaHome(List<string> candidates)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(javaHome)) return;

        var exe = Path.Combine(javaHome, "bin", JavaExeName);
        if (File.Exists(exe)) candidates.Add(exe);
    }

    /// <summary>What a Java executable is, as far as the command line the app builds cares.</summary>
    /// <param name="Major">The feature release (8, 17, 21, 25…). 0 when it could not be read.</param>
    /// <param name="IsOpenJ9">An Eclipse OpenJ9 VM (IBM Semeru) rather than HotSpot: it rejects
    /// HotSpot's <c>-XX:</c> tuning options, so the memory profile gives it only the heap size.</param>
    /// <param name="Is64Bit">False for a 32-bit JVM, which cannot have a heap much above 1 GB.</param>
    public record JavaRuntime(int Major, bool IsOpenJ9, bool Is64Bit)
    {
        /// <summary>A Java that could not be asked: no extra flags, no cap.</summary>
        public static readonly JavaRuntime Unknown = new(0, false, true);
    }

    /// <summary>What each Java executable answered, keyed by its path and stamped with its file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (long Length, DateTime Written, JavaRuntime Runtime)>
        Versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The major version (8, 17, 21, 25...) of a Java executable. 0 on failure.</summary>
    public int GetMajorVersion(string javaExe) => GetRuntime(javaExe).Major;

    /// <summary>The version, VM and word size of a Java executable. <see cref="JavaRuntime.Unknown"/> on failure.</summary>
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
    public JavaRuntime GetRuntime(string javaExe)
    {
        var file = StampOf(javaExe);
        if (file is not null && Versions.TryGetValue(file.FullName, out var known)
            && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
            return known.Runtime;

        // It prints to standard error; both streams are read, under a deadline that holds.
        var result = ProcessRunner.Run(javaExe, ["-version"], TimeSpan.FromSeconds(5));
        var runtime = ParseRuntime(result.Error + result.Output);
        if (file is not null && runtime.Major > 0)
            Versions[file.FullName] = (file.Length, file.LastWriteTimeUtc, runtime);
        return runtime;
    }

    /// <summary>The file behind a Java path, to stamp what it answered with. Null for a bare "java".</summary>
    private static FileInfo? StampOf(string javaExe)
    {
        try { return File.Exists(javaExe) ? new FileInfo(javaExe) : null; }
        catch { return null; /* an unusable path: asked, not remembered */ }
    }

    /// <summary>Reads what <c>java -version</c> printed.</summary>
    /// <remarks>
    /// Every 64-bit JVM says so on its VM line ("64-Bit Server VM", OpenJ9's "amd64-64-Bit"); a
    /// 32-bit one names its VM ("Client VM", "Server VM") without it. Output with no VM line at all
    /// is not taken as 32-bit: capping a heap on a guess would do more harm than not capping it.
    /// </remarks>
    internal static JavaRuntime ParseRuntime(string versionOutput)
    {
        var m = VersionRegex().Match(versionOutput);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major)) return JavaRuntime.Unknown;
        // "1.8" => Java 8
        if (major == 1 && m.Groups[2].Success)
            major = int.TryParse(m.Groups[2].Value, out var minor) ? minor : 0;

        var openJ9 = versionOutput.Contains("OpenJ9", StringComparison.OrdinalIgnoreCase)
                     || versionOutput.Contains("J9 VM", StringComparison.OrdinalIgnoreCase);
        var is64 = versionOutput.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)
                   || !VmLineRegex().IsMatch(versionOutput);
        return new JavaRuntime(major, openJ9, is64);
    }

    [GeneratedRegex(@"\bVM\b")]
    private static partial Regex VmLineRegex();

    /// <summary>What each Java said about a set of flags, keyed by its stamp and the flags.</summary>
    private static readonly ConcurrentDictionary<string, bool> FlagAnswers = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether this Java starts with <paramref name="flags"/>: <c>java &lt;flags&gt; -version</c>
    /// exits cleanly.
    /// </summary>
    /// <remarks>
    /// A JVM refuses to start at all over an option it does not know, or one that needs an unlock
    /// flag it was not given. The memory profile only uses options that are final in the versions it
    /// gives them to, but a vendor build can still differ, and the price of being wrong is a server
    /// that does not start. Asking costs one short JVM run per Java and set of flags; the answer is
    /// remembered against the file like the version is, and for a bare "java" for as long as the app
    /// runs.
    /// </remarks>
    public bool AcceptsFlags(string javaExe, IReadOnlyList<string> flags)
    {
        var file = StampOf(javaExe);
        var key = (file is null ? javaExe : $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")
                  + "|" + string.Join(' ', flags);
        if (FlagAnswers.TryGetValue(key, out var known)) return known;

        var result = ProcessRunner.Run(javaExe, [.. flags, "-version"], TimeSpan.FromSeconds(10));
        var accepted = result.ExitCode == 0;
        // A Java that could not run at all says nothing about the flags; asked again next time.
        if (result.ExitCode != -1) FlagAnswers[key] = accepted;
        return accepted;
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
                .OrderByDescending(d => string.Equals(Path.GetFileName(d), gameVersion,
                    StringComparison.OrdinalIgnoreCase));

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
    public async Task<string> EnsureJavaAsync(int requiredMajor, IProgress<string>? log,
        CancellationToken ct = default)
    {
        // Off the caller's thread: it starts a JVM per installation found, and the caller is the UI.
        var installed = await Task.Run(DetectInstalled, ct);
        var match = installed.FirstOrDefault(i => IsCompatible(i.Major, requiredMajor));
        if (match is not null)
        {
            log?.Report(Localizer.Format("Msg_JavaCompatibleFound", match.Major));
            return match.Path;
        }

        var download = DownloadableMajor(requiredMajor);
        log?.Report(Localizer.Format("Msg_JavaNotCompatibleDownloading", download));
        return await DownloadAdoptiumAsync(download, log, ct);
    }

    /// <summary>
    /// The Adoptium architectures to ask for, in order, on a machine of <paramref name="arch"/>.
    /// </summary>
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
    internal static IReadOnlyList<string> AdoptiumArchitectures(Architecture arch, bool emulatesX64)
        => arch switch
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

    /// <summary>One download of each Java at a time, in this process.</summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> DownloadGates = new();

    /// <summary>Runs <paramref name="work"/> once at a time per major version of Java.</summary>
    /// <remarks>
    /// Two servers that need the same Java — started together with the app, or one after the other
    /// while the first still downloads — shared one archive path and one <c>.partial</c> folder, and
    /// each deleted what the other was writing. The second now waits, and finds the Java installed.
    /// </remarks>
    internal static async Task<T> OneAtATimeAsync<T>(int major, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = DownloadGates.GetOrAdd(major, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await work(); }
        finally { gate.Release(); }
    }

    private Task<string> DownloadAdoptiumAsync(int major, IProgress<string>? log, CancellationToken ct) =>
        OneAtATimeAsync(major, () => DownloadAdoptiumCoreAsync(major, log, ct), ct);

    private async Task<string> DownloadAdoptiumCoreAsync(int major, IProgress<string>? log,
        CancellationToken ct)
    {
        var target = Path.Combine(ManagedRoot, $"jre-{major}");

        // Already installed by us before?
        var existing = FindJavaExe(target);
        if (existing is not null) return existing;

        var package = await FindAdoptiumPackageAsync(major, ct);
        Directory.CreateDirectory(ManagedRoot);
        var isZip = OperatingSystem.IsWindows();
        var archivePath = Path.Combine(ManagedRoot, $"jre-{major}" + (isZip ? ".zip" : ".tar.gz"));

        await DownloadVerifiedArchiveAsync(package, archivePath, log, ct);
        await UnpackIntoPlaceAsync(archivePath, target, isZip, log, ct);

        var javaExe = FindJavaExe(target)
            ?? throw new UserFacingException(Localizer.Get("Msg_JavaExeNotFound"));

        // Make sure the java binary is executable on Unix (tar usually preserves this, but be safe).
        FileModes.TryMakeExecutable(javaExe);

        log?.Report(Localizer.Format("Msg_JavaInstalled", major));
        return javaExe;
    }

    /// <summary>Where Adoptium's builds are served from: GitHub releases, or its own site.</summary>
    private static readonly string[] AdoptiumDownloadHosts = ["github.com", "adoptium.net"];

    /// <summary>A JRE is under 100 MB; this only stops a download that never ends.</summary>
    private const long MaxJreBytes = 400L << 20;

    /// <summary>Where a JRE can be had, and the checksum it must match.</summary>
    private sealed record AdoptiumPackage(string Link, string Checksum);

    /// <summary>
    /// Asks Adoptium for this major's JRE for this machine, trying each architecture it can run.
    /// </summary>
    private async Task<AdoptiumPackage> FindAdoptiumPackageAsync(int major, CancellationToken ct)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
        var archs = AdoptiumArchitectures(RuntimeInformation.OSArchitecture,
            emulatesX64: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

        string? link = null;
        string? checksum = null;
        foreach (var arch in archs)
        {
            var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                         $"?architecture={arch}&image_type=jre&os={os}&vendor=eclipse";
            (link, checksum) = FirstPackage(
                await AppHttp.GetStringCappedAsync(Http, apiUrl, AppHttp.MaxApiBytes, ct));
            if (!string.IsNullOrEmpty(link)) break;
        }
        if (string.IsNullOrEmpty(link))
            throw new UserFacingException(Localizer.Format("Msg_JavaNoDownloadFmt",
                major, $"{os}/{string.Join("/", archs)}"));

        // No checksum, no Java. This archive is extracted and then EXECUTED to run the user's
        // servers, so verifying it cannot be best-effort the way it was: a response without the
        // field meant downloading, unpacking and running a JRE that nothing had checked. Adoptium
        // publishes a checksum for every binary, so a missing one is a broken download path, not a
        // normal condition — the same rule the Forge installer already follows.
        if (string.IsNullOrEmpty(checksum))
            throw new UserFacingException(Localizer.Get("Msg_JavaNoChecksum"));

        // The link comes out of the response, and what it points at is about to be unpacked and
        // run: it has to be on the host Adoptium publishes its builds from.
        AppHttp.RequireAllowedUrl(link, AdoptiumDownloadHosts);

        return new AdoptiumPackage(link, checksum);
    }

    /// <summary>Downloads the archive to <paramref name="archivePath"/>, keeping it only if its SHA-256 matches.</summary>
    private async Task DownloadVerifiedArchiveAsync(AdoptiumPackage package, string archivePath,
        IProgress<string>? log, CancellationToken ct)
    {
        log?.Report(Localizer.Get("Msg_JavaDownloading"));
        using var resp = await Http.GetAsync(package.Link, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await AtomicDownload.ToFileAsync(resp.Content, archivePath,
            verifyAsync: async (part, token) =>
            {
                log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
                await DownloadVerifier.VerifyAsync(part, package.Checksum, HashAlgorithmName.SHA256, token);
            },
            ct: ct,
            maxBytes: MaxJreBytes);
    }

    /// <summary>Unpacks the archive beside <paramref name="target"/> and moves the result into place whole.</summary>
    private static async Task UnpackIntoPlaceAsync(string archivePath, string target, bool isZip,
        IProgress<string>? log, CancellationToken ct)
    {
        log?.Report(Localizer.Get("Msg_JavaInstalling"));
        // Unpacked beside the target and moved into place whole. Unpacking straight into it left a
        // half-extracted runtime after an interruption, and since an existing jre-N folder with a
        // java in it counts as installed, that broken runtime was used from then on.
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
    }

    private static async Task ExtractTarGzAsync(string targzPath, string destDir, CancellationToken ct)
    {
        await using var fs = File.OpenRead(targzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, destDir, overwriteFiles: true, ct);
    }

    /// <summary>
    /// Resolves the 'java' executable on PATH (Linux/macOS) via 'which'. Null if not found.
    /// </summary>
    private static string? WhichJava()
    {
        var result = ProcessRunner.Run("which", ["java"], TimeSpan.FromSeconds(3));
        var path = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
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
