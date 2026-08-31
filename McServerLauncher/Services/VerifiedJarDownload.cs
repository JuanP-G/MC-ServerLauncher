using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Fetching a server jar: announce the size, download atomically, verify, say it is done.
/// </summary>
/// <remarks>
/// <para>
/// Paper and Purpur had a copy each, identical apart from the record type and the hash algorithm —
/// and the algorithm is the one part that genuinely differs, because Purpur publishes only an MD5
/// while Paper publishes SHA-256. Everything around it, including the three progress messages, was
/// the same text twice.
/// </para>
/// <para>
/// The hash being optional is deliberate and unchanged: a build with no published checksum is
/// downloaded without one rather than refused, because HTTPS from the project's own API is what
/// authenticates it and the checksum is here to catch a truncated file. That is the opposite of the
/// rule for mods and for Geyser's own artifacts, where a missing checksum means no install — those
/// call their own paths and are not affected by this.
/// </para>
/// </remarks>
public static class VerifiedJarDownload
{
    public static async Task ToFileAsync(HttpClient http, string url, string destPath,
        string? expectedHash, HashAlgorithmName algorithm, IProgress<string>? log,
        CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        log?.Report(totalMb > 0
            ? string.Format(Localizer.Get("Msg_DownloadingJarSize"), totalMb.ToString("0.#"))
            : Localizer.Get("Msg_DownloadingJar"));

        await AtomicDownload.ToFileAsync(resp.Content, destPath,
            verifyAsync: async (part, token) =>
            {
                if (string.IsNullOrEmpty(expectedHash)) return;
                log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
                await DownloadVerifier.VerifyAsync(part, expectedHash, algorithm, token);
            },
            ct: ct);

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
    }
}
