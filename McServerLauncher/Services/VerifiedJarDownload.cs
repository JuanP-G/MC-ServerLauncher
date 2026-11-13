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
/// Paper, Purpur and the vanilla server.jar had a copy each, identical apart from the record type and
/// the hash algorithm — and the algorithm is the one part that genuinely differs: Purpur publishes
/// only an MD5, Paper a SHA-256 and Mojang a SHA-1. Everything around it, including the three
/// progress messages, was the same text three times.
/// </para>
/// <para>
/// Whether a missing checksum is tolerated is the caller's decision (<c>requireChecksum</c>), because
/// the sources differ. Paper and Mojang always publish one, so its absence means the answer was not
/// understood and the jar is not installed — the same rule mods and Geyser's artifacts follow.
/// Purpur's MD5 is the only one it has and can be missing, so there the jar is downloaded without
/// one: HTTPS from the project's own host is what authenticates it, and the checksum is only there to
/// catch a truncated file.
/// </para>
/// <para>
/// The address is checked against <c>allowedHosts</c> before the request and again on the answer
/// that came back after redirects (<see cref="AppHttp.RequireAllowedFinalUrl"/>): the Paper and
/// vanilla download links arrive inside an API response, and a response must not be able to send the
/// app to a host the project does not own.
/// </para>
/// </remarks>
public static class VerifiedJarDownload
{
    /// <summary>No server jar is anywhere near this; it only stops one that never ends.</summary>
    private const long MaxJarBytes = 400L << 20;

    /// <exception cref="UserFacingException">
    /// The address is not on <paramref name="allowedHosts"/>, or a checksum was required and absent.
    /// </exception>
    public static async Task ToFileAsync(HttpClient http, string url, string destPath,
        string? expectedHash, HashAlgorithmName algorithm, IProgress<string>? log,
        string[] allowedHosts, bool requireChecksum, CancellationToken ct = default)
    {
        AppHttp.RequireAllowedUrl(url, allowedHosts);
        if (requireChecksum && string.IsNullOrEmpty(expectedHash))
            throw new UserFacingException(Localizer.Format(
                "Msg_JarNoChecksumFmt", AtomicDownload.DisplayName(destPath)));

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        AppHttp.RequireAllowedFinalUrl(resp, allowedHosts);

        var totalMb = (resp.Content.Headers.ContentLength ?? 0) / (1024.0 * 1024.0);
        log?.Report(totalMb > 0
            ? Localizer.Format("Msg_DownloadingJarSize", totalMb.ToString("0.#"))
            : Localizer.Get("Msg_DownloadingJar"));

        await AtomicDownload.ToFileAsync(resp.Content, destPath,
            verifyAsync: async (part, token) =>
            {
                if (!string.IsNullOrEmpty(expectedHash)) log?.Report(Localizer.Get("Msg_VerifyingChecksum"));
                await DownloadVerifier.VerifyIfProvidedAsync(part, expectedHash, algorithm, token);
            },
            ct: ct,
            maxBytes: MaxJarBytes);

        log?.Report(Localizer.Get("Msg_DownloadComplete"));
    }
}
