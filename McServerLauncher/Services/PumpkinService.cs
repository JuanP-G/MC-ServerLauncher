using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Finds and downloads Pumpkin, a Minecraft server written in Rust
/// (<see href="https://github.com/Pumpkin-MC/Pumpkin"/>).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is like the Java servers. Pumpkin is published as one executable per operating
/// system on its GitHub releases, and a release is tied to one version of the game: the tag
/// <c>0.2.2+26.3-26.60</c> is Pumpkin 0.2.2 for Java Edition 26.3 and Bedrock 26.60. So there is no
/// Minecraft version to choose — the release chooses it.
/// </para>
/// <para>
/// Only the latest <em>stable</em> release is offered. The <c>nightly</c> and <c>canary</c> tags are
/// prereleases, GitHub's "latest" skips them, and they publish no checksum file.
/// </para>
/// <para>
/// Verified like every other download: each stable release carries a <c>checksums.sha256</c> with one
/// line per file, and a release without it, or without a line for the file wanted, is refused
/// rather than installed unchecked.
/// </para>
/// </remarks>
public partial class PumpkinService
{
    private const string LatestRelease = "https://api.github.com/repos/Pumpkin-MC/Pumpkin/releases/latest";
    private const string ChecksumsFile = "checksums.sha256";

    /// <summary>Where a release's files may come from. Anything else in the API's answer is refused.</summary>
    private static readonly string[] DownloadHosts = ["github.com"];

    private static readonly HttpClient Http = AppHttp.Create(TimeSpan.FromMinutes(10));

    /// <summary>One stable release, narrowed to the file this machine runs.</summary>
    /// <param name="Version">Pumpkin's own version, "0.2.2".</param>
    /// <param name="GameVersion">The Java Edition version it serves, "26.3".</param>
    /// <param name="AssetName">The file on the release, "pumpkin-X64-Windows.exe".</param>
    /// <param name="Url">Where to download it.</param>
    /// <param name="Sha256">Its hash, from the release's own <c>checksums.sha256</c>.</param>
    public record PumpkinRelease(string Version, string GameVersion, string AssetName, string Url, string Sha256);

    /// <summary>What the server's file is called once it is in the server folder.</summary>
    /// <remarks>
    /// One fixed name, not the release's: <see cref="ServerDetectionService"/> and a later update
    /// both need to find it, and "pumpkin-X64-Windows.exe" says nothing the folder does not.
    /// </remarks>
    public static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pumpkin.exe" : "pumpkin";

    /// <summary>The latest stable release for this machine.</summary>
    /// <exception cref="UserFacingException">
    /// There is no build for this system, or the release cannot be verified.
    /// </exception>
    public async Task<PumpkinRelease> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        var json = await AppHttp.GetStringCappedAsync(Http, LatestRelease, AppHttp.MaxApiBytes, ct);
        var (tag, assets) = ParseRelease(json);

        var wanted = AssetNameFor(CurrentOs(), RuntimeInformation.OSArchitecture);
        if (wanted is null || !assets.TryGetValue(wanted, out var url))
            throw new UserFacingException(Localizer.Format("Msg_PumpkinNoBuildFmt", tag));

        if (!assets.TryGetValue(ChecksumsFile, out var sumsUrl))
            throw new UserFacingException(Localizer.Format("Msg_PumpkinNoChecksumFmt", tag));

        AppHttp.RequireAllowedUrl(url, DownloadHosts);
        AppHttp.RequireAllowedUrl(sumsUrl, DownloadHosts);

        var sums = await AppHttp.GetStringCappedAsync(Http, sumsUrl, AppHttp.MaxSmallTextBytes, ct);
        var hash = ChecksumFor(sums, wanted)
            ?? throw new UserFacingException(Localizer.Format("Msg_PumpkinNoChecksumFmt", tag));

        var (version, game) = ParseTag(tag);
        return new PumpkinRelease(version, game, wanted, url, hash);
    }

    /// <summary>Downloads the release's executable, checks it, and makes it runnable.</summary>
    public async Task DownloadAsync(PumpkinRelease release, string destPath, IProgress<string>? log,
        CancellationToken ct = default)
    {
        await VerifiedJarDownload.ToFileAsync(Http, release.Url, destPath, release.Sha256,
            HashAlgorithmName.SHA256, log, ct);

        // A downloaded file is not executable on Linux or macOS until somebody says so.
        FileModes.TryMakeExecutable(destPath);
    }

    /// <summary>The operating systems Pumpkin publishes a build for.</summary>
    public enum Os
    {
        Windows,
        Linux,
        MacOs
    }

    private static Os? CurrentOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Os.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Os.Linux
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Os.MacOs
        : null;

    /// <summary>The release file for a system, or null when Pumpkin has none for it.</summary>
    /// <remarks>
    /// The plain builds on purpose. The <c>-v3</c> ones need a processor with AVX2 and die with an
    /// illegal instruction on one without; <c>-musl</c> is for systems without glibc, which the app
    /// itself does not run on.
    /// </remarks>
    internal static string? AssetNameFor(Os? os, Architecture architecture)
    {
        var arch = architecture switch
        {
            Architecture.X64 => "X64",
            Architecture.Arm64 => "ARM64",
            _ => null
        };
        if (arch is null) return null;

        return os switch
        {
            Os.Windows => $"pumpkin-{arch}-Windows.exe",
            Os.Linux => $"pumpkin-{arch}-Linux",
            Os.MacOs => $"pumpkin-{arch}-macOS",
            _ => null
        };
    }

    /// <summary>The tag and the files of a release, from GitHub's answer.</summary>
    internal static (string Tag, Dictionary<string, string> Assets) ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
            throw new UserFacingException(Localizer.Get("Msg_PumpkinNoRelease"));

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets[name] = url;
            }
        }

        return (tag, assets);
    }

    /// <summary>The hash for one file out of a <c>checksums.sha256</c>, or null when it has none.</summary>
    /// <remarks>
    /// The format is <c>sha256sum</c>'s: the hash, whitespace, an optional <c>*</c>, the file name.
    /// The name must match whole — "pumpkin-X64-Linux" is also how "pumpkin-X64-Linux-musl" starts.
    /// </remarks>
    internal static string? ChecksumFor(string checksums, string fileName)
    {
        foreach (var raw in checksums.Split('\n'))
        {
            var match = ChecksumLine().Match(raw.Trim());
            if (match.Success && match.Groups["name"].Value == fileName)
                return match.Groups["hash"].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Pumpkin's version and the Java Edition version out of a tag like <c>0.2.2+26.3-26.60</c>.</summary>
    /// <remarks>
    /// A tag in another shape keeps the whole of it as the version and leaves the game version
    /// empty: the server still installs and runs, the card just cannot say which Minecraft it is.
    /// </remarks>
    internal static (string Version, string GameVersion) ParseTag(string tag)
    {
        var match = Tag().Match(tag);
        return match.Success
            ? (match.Groups["version"].Value, match.Groups["java"].Value)
            : (tag, string.Empty);
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S+)$")]
    private static partial Regex ChecksumLine();

    [GeneratedRegex(@"^(?<version>[^+]+)\+(?<java>\d+(?:\.\d+)+)-(?<bedrock>.+)$")]
    private static partial Regex Tag();
}
