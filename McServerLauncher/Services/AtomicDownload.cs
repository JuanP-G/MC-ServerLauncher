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
    /// </remarks>
    public static string PathIn(string folder, string remoteFileName)
    {
        var name = Path.GetFileName(remoteFileName);
        if (string.IsNullOrWhiteSpace(name))
            throw new UserFacingException(
                string.Format(Localizer.Get("Msg_BadRemoteFileNameFmt"), remoteFileName));

        return Path.Combine(folder, name);
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
    /// </summary>
    public static async Task ToFileAsync(HttpContent content, string destPath,
        Func<string, CancellationToken, Task>? verifyAsync = null,
        IProgress<double>? progress = null,
        long maxBytes = DefaultMaxBytes,
        CancellationToken ct = default)
    {
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
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
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
        new(string.Format(Localizer.Get("Msg_DownloadTooLargeFmt"), DisplayName(destPath),
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
