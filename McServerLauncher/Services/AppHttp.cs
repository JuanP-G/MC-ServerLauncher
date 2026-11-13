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
    /// How long a body may go without sending a single byte before the read is given up on.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> stops counting when the headers arrive if the request was
    /// sent with <c>ResponseHeadersRead</c> (every download here is), and the clients that download
    /// have an infinite or a ten-minute one anyway. A connection that goes quiet after the headers
    /// then waited for ever: the update box stayed on "Downloading", and the news check, which holds
    /// a lock while it reads, made every later check queue behind it. A pause is not a slow link:
    /// the clock restarts with every read that returns data, so a download that is merely slow is
    /// never cut.
    /// </remarks>
    public static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One read from <paramref name="stream"/> that gives up after <paramref name="idle"/> without data.
    /// </summary>
    /// <exception cref="TimeoutException">Nothing arrived for <paramref name="idle"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal static async ValueTask<int> ReadWithDeadlineAsync(Stream stream, Memory<byte> buffer,
        TimeSpan idle, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(idle);
        try
        {
            return await stream.ReadAsync(buffer, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("the connection stopped sending data");
        }
    }

    /// <summary>
    /// Reads a response body, refusing one that grows past <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// <c>GetStringAsync</c> reads whatever the server sends, so a server that answers with
    /// gigabytes fills memory before any check on the result can run. The announced length is
    /// refused up front, and the bytes actually read are counted as well, since the announcement
    /// can be absent or false. A body that stops arriving for <paramref name="idleTimeout"/>
    /// (<see cref="ReadIdleTimeout"/> when null) is given up on.
    /// </remarks>
    /// <exception cref="InvalidDataException">The body is larger than the limit.</exception>
    /// <exception cref="TimeoutException">The server stopped sending.</exception>
    public static async Task<byte[]> ReadCappedAsync(HttpContent content, long limit, CancellationToken ct,
        TimeSpan? idleTimeout = null)
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException("response too large");

        var idle = idleTimeout ?? ReadIdleTimeout;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await ReadWithDeadlineAsync(stream, chunk, idle, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("response too large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>A GET whose body is read as text, up to <paramref name="limit"/> bytes.</summary>
    /// <exception cref="HttpRequestException">The server answered with an error status.</exception>
    /// <exception cref="InvalidDataException">The body is larger than the limit.</exception>
    /// <exception cref="TimeoutException">The server stopped sending.</exception>
    public static async Task<string> GetStringCappedAsync(HttpClient http, string url, long limit,
        CancellationToken ct, TimeSpan? idleTimeout = null)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await ReadCappedStringAsync(resp.Content, limit, ct, idleTimeout);
    }

    /// <summary>A response body as text (UTF-8, or what its byte-order mark says), up to <paramref name="limit"/> bytes.</summary>
    public static async Task<string> ReadCappedStringAsync(HttpContent content, long limit, CancellationToken ct,
        TimeSpan? idleTimeout = null)
    {
        var bytes = await ReadCappedAsync(content, limit, ct, idleTimeout);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>A response body deserialised from JSON, up to <paramref name="limit"/> bytes.</summary>
    /// <remarks>The cap-aware stand-in for <c>ReadFromJsonAsync</c>, with the same (web) defaults.</remarks>
    public static async Task<T?> ReadJsonCappedAsync<T>(HttpContent content, long limit, CancellationToken ct,
        TimeSpan? idleTimeout = null)
    {
        var bytes = await ReadCappedAsync(content, limit, ct, idleTimeout);
        return System.Text.Json.JsonSerializer.Deserialize<T>(bytes, System.Text.Json.JsonSerializerOptions.Web);
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
        throw new UserFacingException(Localizer.Format("Msg_DownloadHostRefusedFmt", shown));
    }

    /// <summary>
    /// Throws unless the address the answer actually came from, after any redirects, is on
    /// <paramref name="hosts"/>. Call it on a response opened with <c>ResponseHeadersRead</c>, before
    /// its body is read.
    /// </summary>
    /// <remarks>
    /// <see cref="RequireAllowedUrl"/> checks the address that was asked for; the client then follows
    /// up to fifty redirects on its own, so a trusted host could still hand the request to an
    /// untrusted one, and only the first hop had been looked at.
    /// </remarks>
    /// <exception cref="UserFacingException">The answer came from a host that is not allowed.</exception>
    public static void RequireAllowedFinalUrl(HttpResponseMessage response, params string[] hosts)
    {
        var final = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (final is null) return;     // no request recorded (a test double): nothing to check
        RequireAllowedUrl(final, hosts);
    }

    /// <summary>
    /// How long a pooled connection is reused. The clients are static and live as long as the app,
    /// and a connection that is never retired keeps talking to the address DNS gave it on the first
    /// request, so a long session never notices that a host has moved.
    /// </summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A client with this app's User-Agent and the given overall timeout.</summary>
    /// <param name="timeout">How long one request may take in all. Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> when the caller sets its own deadline.</param>
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}
