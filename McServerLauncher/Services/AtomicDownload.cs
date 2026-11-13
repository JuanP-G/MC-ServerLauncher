using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Downloads a file so that a failure can never damage what was already on disk.
/// <para>
/// Writing straight to the destination truncates it the moment the stream opens, long before the
/// download is known to be good. A connection dropped halfway then leaves a valid path holding a
/// half a file — and for the Fabric server jar it was worse than that: the structural check that
/// runs afterwards deletes what it rejects, so an interrupted loader change turned a working
/// server into one with no jar to start at all.
/// </para>
/// <para>
/// So the bytes go to "&lt;dest&gt;.part", verification runs against that, and only a file that
/// arrived complete and passed its check replaces the real one — in a single filesystem move. This
/// is the same guarantee <see cref="AtomicJsonFile"/> gives the app's JSON, applied to binaries.
/// </para>
/// </summary>
public static class AtomicDownload
{
    private const string PartSuffix = ".part";

    /// <summary>
    /// How big a download may be unless its caller knows better: a mod, the biggest thing the app
    /// fetches, is far below it. Callers whose file has a known order of magnitude (a JRE, an
    /// installer, a server jar) pass a lower one.
    /// </summary>
    public const long DefaultMaxBytes = 1L << 30;

    /// <summary>
    /// Where a file named by a remote server is allowed to land: inside
    /// <paramref name="folder"/>, always.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Path.Combine(folder, name)</c> is not a containment check and does not pretend to be one.
    /// Handed <c>"../../evil.jar"</c> it walks up; handed an absolute path it discards the folder
    /// entirely and returns that path. The name comes from Modrinth or from GeyserMC's downloads
    /// API, so nothing hostile is expected there — which is exactly the reasoning that makes a
    /// boundary get skipped once and then five more times.
    /// </para>
    /// <para>
    /// This exists so the join happens in one place. Every download the app performs goes through
    /// it, and a name with no usable last segment is refused rather than guessed at: it can only
    /// mean the API said something the app does not understand.
    /// </para>
    /// <para>
    /// Staying inside the folder is not the same as being a file name Windows will store as written.
    /// <c>NUL.jar</c> and <c>COM1.jar</c> open a device instead of creating a file, <c>mod.jar:x</c>
    /// writes an alternate data stream of <c>mod.jar</c>, and a name ending in a dot or a space is
    /// silently stored without it, so the file the app wrote is not the one it later looks for. Those
    /// are refused too, on every platform, so a folder that is synced or moved keeps working.
    /// When <paramref name="requiredExtension"/> is given the name must end in it with something
    /// before it: what goes in the mods folder is a <c>.jar</c>, and an API that offers a
    /// <c>.bat</c> there is saying something the app does not understand.
    /// </para>
    /// </remarks>
    /// <exception cref="UserFacingException">The name cannot be used as a file name here.</exception>
    public static string PathIn(string folder, string remoteFileName, string? requiredExtension = null)
    {
        var name = Path.GetFileName(remoteFileName);
        if (!IsSafeFileName(name) ||
            (requiredExtension is not null &&
             (name.Length <= requiredExtension.Length ||
              !name.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))))
            throw new UserFacingException(
                Localizer.Format("Msg_BadRemoteFileNameFmt", remoteFileName));

        return Path.Combine(folder, name);
    }

    /// <summary>Longest file name accepted: far above any real mod, well below the 255 a filesystem allows.</summary>
    private const int MaxFileNameLength = 200;

    /// <summary>Names Windows treats as devices, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Whether <paramref name="name"/> is one plain file name that every filesystem here stores as written.</summary>
    internal static bool IsSafeFileName([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameLength) return false;
        if (name is "." or "..") return false;
        if (name != name.Trim() || name.EndsWith('.')) return false;

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in name)
            if (c < ' ' || ":*?\"<>|\\/".Contains(c) || invalid.Contains(c))
                return false;

        // The device name is what comes before the first dot ("nul.tar.jar" is still the device),
        // and Windows ignores spaces between it and the dot ("nul .jar").
        var stem = name.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(stem);
    }

    /// <summary>
    /// Streams <paramref name="content"/> into <paramref name="destPath"/> atomically.
    /// <para>
    /// <paramref name="verifyAsync"/> is handed the path of the *temporary* file, so anything it
    /// rejects (a checksum mismatch, a jar with the wrong structure) never reaches the real one.
    /// It runs with the write handle already closed, because <see cref="File.Create(string)"/>
    /// opens with <c>FileShare.None</c> and a reader would otherwise hit a sharing violation on
    /// Windows. Throwing from it aborts the download and leaves the destination untouched.
    /// </para>
    /// <para>
    /// <paramref name="maxBytes"/> is a ceiling, not a size check: a server that never stops sending
    /// would otherwise fill the disk. A file announced as larger is refused before a byte is
    /// written, and one that turns out larger than it said is cut off and removed.
    /// </para>
    /// <para>
    /// A download that stops sending for <paramref name="idleTimeout"/>
    /// (<see cref="AppHttp.ReadIdleTimeout"/> when null) is abandoned with a <see cref="TimeoutException"/>
    /// and the partial file removed: the client's own timeout stops counting once the headers arrive,
    /// so without this a connection that went quiet halfway waited for ever.
    /// </para>
    /// </summary>
    public static async Task ToFileAsync(HttpContent content, string destPath,
        Func<string, CancellationToken, Task>? verifyAsync = null,
        IProgress<double>? progress = null,
        long maxBytes = DefaultMaxBytes,
        CancellationToken ct = default,
        TimeSpan? idleTimeout = null)
    {
        var idle = idleTimeout ?? AppHttp.ReadIdleTimeout;
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Beside the destination on purpose: File.Move is only atomic within one volume, and the
        // system temp folder is regularly on another one.
        var partPath = destPath + PartSuffix;

        try
        {
            var total = content.Headers.ContentLength;
            if (total > maxBytes) throw TooLarge(destPath, maxBytes);

            await using (var file = File.Create(partPath))
            await using (var source = await content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await AppHttp.ReadWithDeadlineAsync(source, buffer, idle, ct)) > 0)
                {
                    received += read;
                    if (received > maxBytes) throw TooLarge(destPath, maxBytes);

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (progress is not null && total is > 0) progress.Report((double)received / total.Value);
                }
            }

            if (verifyAsync is not null) await verifyAsync(partPath, ct);

            File.Move(partPath, destPath, overwrite: true);
        }
        catch
        {
            FileCleanup.TryDelete(partPath);
            throw;
        }
    }

    private static UserFacingException TooLarge(string destPath, long maxBytes) =>
        new(Localizer.Format("Msg_DownloadTooLargeFmt", DisplayName(destPath),
            $"{maxBytes / (1024.0 * 1024.0):0} MB"));

    /// <summary>The name to show the user for a file being downloaded to <paramref name="path"/>.</summary>
    /// <remarks>
    /// Verification runs against the ".part" copy, so the raw file name would tell the user their
    /// download of "server.jar.part" failed. This gives back the name they actually recognise.
    /// </remarks>
    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(PartSuffix, StringComparison.Ordinal)
            ? name[..^PartSuffix.Length]
            : name;
    }
}
