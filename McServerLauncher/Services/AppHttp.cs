using System.IO;
using System.Net.Http;
using System.Text;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// The one place an <see cref="HttpClient"/> of this app is made.
/// </summary>
/// <remarks>
/// <para>
/// Seventeen services each built their own, and they had drifted: six sent
/// <c>JuanP-G/MC-ServerLauncher</c> as their User-Agent, <see cref="UpdateService"/> sent
/// <c>MC-ServerLauncher</c>, and the rest sent none. Some CDNs (Forge's maven among them) refuse a
/// request without one, and Modrinth asks for one that says who is calling.
/// </para>
/// <para>
/// Keeping the creation here is also what gives the rules about what may come back a single home:
/// a ceiling on how much a response may weigh (<see cref="ReadCappedAsync"/>) and the hosts a
/// download is allowed to come from (<see cref="RequireAllowedUrl"/>) belong to every client or to
/// none.
/// </para>
/// <para>
/// Each service still keeps its own static client (one per service, with its own timeout), so
/// nothing is shared that did not need to be: a slow download cannot hold up the version check.
/// </para>
/// </remarks>
public static class AppHttp
{
    /// <summary>What every request says it is, so the services we call can tell who is asking.</summary>
    public const string UserAgent = "JuanP-G/MC-ServerLauncher";

    /// <summary>The most a JSON answer from an API is read for: far above any real one.</summary>
    public const long MaxApiBytes = 8L << 20;

    /// <summary>The most a checksum list or a signature is read for; the real ones are a few hundred bytes.</summary>
    public const long MaxSmallTextBytes = 64L << 10;

    /// <summary>
    /// Reads a response body, refusing one that grows past <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// <c>GetStringAsync</c> reads whatever the server sends, so a server that answers with
    /// gigabytes fills memory before any check on the result can run. The announced length is
    /// refused up front, and the bytes actually read are counted as well, since the announcement
    /// can be absent or false.
    /// </remarks>
    /// <exception cref="InvalidDataException">The body is larger than the limit.</exception>
    public static async Task<byte[]> ReadCappedAsync(HttpContent content, long limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException("response too large");

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("response too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>A GET whose body is read as text, up to <paramref name="limit"/> bytes.</summary>
    /// <exception cref="HttpRequestException">The server answered with an error status.</exception>
    /// <exception cref="InvalidDataException">The body is larger than the limit.</exception>
    public static async Task<string> GetStringCappedAsync(HttpClient http, string url, long limit,
        CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await ReadCappedStringAsync(resp.Content, limit, ct);
    }

    /// <summary>A response body as text (UTF-8, or what its byte-order mark says), up to <paramref name="limit"/> bytes.</summary>
    public static async Task<string> ReadCappedStringAsync(HttpContent content, long limit, CancellationToken ct)
    {
        var bytes = await ReadCappedAsync(content, limit, ct);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Whether <paramref name="url"/> is an https address on one of <paramref name="hosts"/> (or a
    /// subdomain of one), with no credentials in it.
    /// </summary>
    public static bool IsAllowedUrl(string? url, params string[] hosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) return false;
        return hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                              || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Throws unless <paramref name="url"/> passes <see cref="IsAllowedUrl"/>. For a download link
    /// that came out of an API response: the response says where the file is, and nothing in it
    /// should be able to say somewhere other than the publisher's own host.
    /// </summary>
    /// <exception cref="UserFacingException">The address is not https or not on an allowed host.</exception>
    public static void RequireAllowedUrl(string? url, params string[] hosts)
    {
        if (IsAllowedUrl(url, hosts)) return;
        var shown = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url ?? string.Empty;
        throw new UserFacingException(string.Format(Localizer.Get("Msg_DownloadHostRefusedFmt"), shown));
    }

    /// <summary>A client with this app's User-Agent and the given overall timeout.</summary>
    /// <param name="timeout">How long one request may take in all. Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> when the caller sets its own deadline.</param>
    public static HttpClient Create(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}
