using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Answers Bedrock clients on Geyser's port while the real server is stopped, so a player on Bedrock
/// can wake a sleeping server the way a Java player does.
/// <para>
/// Since Bedrock 26.60 a client that wants to join starts with a plain HTTP request over TCP to the
/// server's address, and only afterwards plays over WebRTC on UDP. Geyser answers that request on the
/// same number as its <c>bedrock.port</c>; while the server is down nothing does, the connection is
/// refused, and the client reports a generic error. This listener holds that TCP port instead.
/// </para>
/// <para>
/// It speaks only the two requests of that onboarding: <c>GET /v1/join</c>, the capability check,
/// which gets the server's status; and <c>POST /v1/join/{networkId}</c>, which carries the client's
/// offer and is what wakes the server. It cannot answer the offer for real — that is a WebRTC
/// handshake with a signed identity — so the POST is refused and the player tries again a few
/// seconds later, when Geyser is up.
/// </para>
/// <para>
/// The GET alone does not wake anything: the client may send it just to draw the server, and
/// starting the server (with a backup in front of it) for people who are not even playing is exactly
/// what the Java listener avoids by waiting for Join. With a Playit tunnel this socket is reachable
/// from the internet, so it has the same bounds as <see cref="WakeOnDemandListener"/>: lengths
/// before allocation, a deadline per connection, a cap on how many at once.
/// </para>
/// </summary>
public sealed class NetherNetWakeListener : IDisposable
{
    /// <summary>A request line plus headers is a few hundred bytes; this is not a real client.</summary>
    internal const int MaxHeaderBytes = 8 * 1024;

    /// <summary>An SDP offer with its candidates and identity is a few KB.</summary>
    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>How long a single read may stall. NOT a limit on the connection — see below.</summary>
    private const int ConnectionTimeoutMs = 5000;

    /// <summary>The actual ceiling on a connection's life, from accept to close.</summary>
    private const int ConnectionDeadlineMs = 15000;

    /// <summary>Same as the Java listener: enough for a household, no more.</summary>
    private const int MaxConcurrentConnections = 16;

    /// <summary>
    /// The protocol number put in the status. Informational: the join does not depend on it, since
    /// the answer to the offer is refused anyway.
    /// </summary>
    internal const int ProtocolNumber = 2225;

    private static readonly Regex NetworkId = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex ColourCodes = new(((char)0xA7) + ".", RegexOptions.Compiled);   // the section sign: a Minecraft colour code

    /// <summary>The deadline actually applied. Settable so a test can exercise it in seconds.</summary>
    internal TimeSpan ConnectionDeadline { get; set; } = TimeSpan.FromMilliseconds(ConnectionDeadlineMs);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Func<WakeStatus>? _status;
    private Action? _onJoinAttempt;
    private int _open;

    public bool IsListening => _listener is not null;

    /// <summary>
    /// Starts answering on <paramref name="port"/>. Returns false when the port can't be bound —
    /// the caller keeps working, it just means no Bedrock wake-on-demand for now.
    /// </summary>
    /// <param name="port">Geyser's Bedrock port; the TCP side of it.</param>
    /// <param name="status">What the client is told about the server, asked for on every request.</param>
    /// <param name="onJoinAttempt">
    /// Somebody tried to join. Called on the connection's thread, once per POST.
    /// </param>
    public bool Start(int port, Func<WakeStatus> status, Action onJoinAttempt)
    {
        Stop();

        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            _listener = listener;
            _status = status;
            _onJoinAttempt = onJoinAttempt;
            _cts = new CancellationTokenSource();

            _ = AcceptLoopAsync(listener, _cts.Token);
            return true;
        }
        catch
        {
            // Port already taken (Geyser left running, another app): nothing to do but skip it.
            Stop();
            return false;
        }
    }

    /// <summary>
    /// Releases the port. Must be called before the real server starts, or Geyser cannot open its
    /// own signaling on it.
    /// </summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Stop(); } catch { /* never started */ }
        _listener = null;
        _status = null;
        _onJoinAttempt = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }   // stopped, or the socket died

            if (Interlocked.Increment(ref _open) > MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _open);
                try { client.Close(); } catch { /* nothing to do */ }
                continue;
            }

            // A thread of its own, as in the Java listener: Serve blocks on its reads for up to the
            // deadline, and the pool grows too slowly to let sixteen of those sit on it. The cap
            // above is what keeps the number of these threads bounded.
            _ = Task.Factory.StartNew(() =>
            {
                try { Serve(client); }
                catch { /* malformed client: it just gets no answer */ }
                finally
                {
                    Interlocked.Decrement(ref _open);
                    try { client.Close(); } catch { /* already gone */ }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void Serve(TcpClient client)
    {
        client.ReceiveTimeout = client.SendTimeout = ConnectionTimeoutMs;

        // ReceiveTimeout is not a deadline: every byte that arrives resets it, so a client that
        // dribbles one every few seconds would hold its slot for ever. Closing the socket from the
        // timer makes whichever read is blocked throw, which the catch around Serve already treats
        // as a malformed client.
        using var deadline = new CancellationTokenSource(ConnectionDeadline);
        using var closeOnDeadline = deadline.Token.Register(() =>
        {
            try { client.Close(); } catch { /* already on its way out */ }
        });

        using var stream = client.GetStream();

        var request = ReadRequest(stream);
        if (request is null) return;

        var status = _status?.Invoke();
        if (status is null) return;

        var path = request.Value.Path;

        if (request.Value.Method == "GET" && path == "/v1/join")
        {
            Respond(stream, 200, "OK", "application/json", BuildStatusJson(status));
        }
        else if (request.Value.Method == "POST" && IsJoinPath(path))
        {
            // Waking first: the response below ends the connection, and the point of all this is
            // that the server is already coming up by the time the client gives up and retries.
            _onJoinAttempt?.Invoke();

            DrainBody(stream, request.Value.ContentLength);

            // Anything but a 2xx is a signaling failure to the client, which then does not attempt
            // WebRTC — there is nobody to talk to yet. Retry-After is a hint, nothing relies on it.
            Respond(stream, 503, "Service Unavailable", "text/plain", "The server is starting.",
                "Retry-After: 15\r\n");
        }
        else
        {
            Respond(stream, 404, "Not Found", "text/plain", "Not found.");
        }
    }

    /// <summary><c>/v1/join/{networkId}</c> with an id that looks like one.</summary>
    internal static bool IsJoinPath(string path)
    {
        const string prefix = "/v1/join/";
        return path.StartsWith(prefix, StringComparison.Ordinal)
               && NetworkId.IsMatch(path.AsSpan(prefix.Length).ToString());
    }

    /// <summary>What the client reads on its capability check. Public shape so it can be tested.</summary>
    internal static string BuildStatusJson(WakeStatus status)
    {
        // The first line of the MOTD is the server's own name; the second is the "asleep" notice,
        // which belongs in the Java list but would make an odd name here. Colour codes are for Java
        // clients: Bedrock shows them as literal characters.
        var name = ColourCodes.Replace(status.Description.Split('\n')[0], string.Empty).Trim();

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["protocol"] = ProtocolNumber,
            ["version"] = status.VersionName,
            ["level"] = name,
            ["players"] = 0,
            ["maxPlayers"] = status.MaxPlayers,
            ["gameType"] = 0
        });
    }

    // --- HTTP plumbing -------------------------------------------------------------------------

    /// <summary>The parts of a request that matter here.</summary>
    internal readonly record struct Request(string Method, string Path, long ContentLength);

    /// <summary>
    /// Reads the request line and headers, or null if they are absent, truncated, absurdly long or
    /// not HTTP. The body is left on the stream.
    /// </summary>
    internal static Request? ReadRequest(Stream stream)
    {
        var head = new byte[MaxHeaderBytes];
        var length = 0;
        var complete = false;

        while (length < head.Length)
        {
            var b = stream.ReadByte();
            if (b < 0) return null;

            head[length++] = (byte)b;
            if (length >= 4 && head[length - 4] == '\r' && head[length - 3] == '\n'
                && head[length - 2] == '\r' && head[length - 1] == '\n')
            {
                complete = true;
                break;
            }
        }
        if (!complete) return null;

        var lines = Encoding.ASCII.GetString(head, 0, length).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal)) return null;

        var target = parts[1];
        if (!target.StartsWith('/')) return null;

        var query = target.IndexOf('?');
        var path = query >= 0 ? target[..query] : target;

        long contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!long.TryParse(line[(colon + 1)..].Trim(), out contentLength) || contentLength < 0)
                return null;
        }

        return new Request(parts[0], path, contentLength);
    }

    /// <summary>
    /// Reads and discards the body, up to a bound. Closing with unread data makes the OS reset the
    /// connection, and the client may then never see the response that follows.
    /// </summary>
    private static void DrainBody(Stream stream, long length)
    {
        if (length is <= 0 or > MaxBodyBytes) return;

        var buffer = new byte[4096];
        var remaining = (int)length;
        while (remaining > 0)
        {
            var n = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (n <= 0) return;
            remaining -= n;
        }
    }

    private static void Respond(Stream stream, int code, string reason, string contentType, string body,
        string extraHeaders = "")
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {contentType}\r\n"
                   + $"Content-Length: {payload.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";

        var bytes = Encoding.ASCII.GetBytes(head).Concat(payload).ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }
}
