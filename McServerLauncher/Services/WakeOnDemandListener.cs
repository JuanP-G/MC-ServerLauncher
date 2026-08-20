using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>What a client is told about a server that is currently asleep.</summary>
/// <param name="Description">The two lines shown where the MOTD normally goes.</param>
/// <param name="VersionName">Shown next to the player count, e.g. "1.21.1".</param>
/// <param name="MaxPlayers">The "0/max" the client draws.</param>
/// <param name="IconPath">The server's <c>server-icon.png</c>, or null for the default icon.</param>
/// <param name="DisconnectMessage">Shown full-screen to whoever presses Join.</param>
public record WakeStatus(
    string Description,
    string VersionName,
    int MaxPlayers,
    string? IconPath,
    string DisconnectMessage);

/// <summary>
/// Answers Minecraft clients on the server's port while the real server is stopped, so a stopped
/// server can say "I'm asleep, come in and I'll wake up" instead of just refusing the connection.
/// <para>
/// It speaks the small, stable part of the protocol needed for that: the handshake, the server-list
/// status, and the login disconnect. Pressing Join is what wakes the server — the client re-pings
/// every few seconds while the multiplayer screen is open, so waking on a status request would
/// start the server over and over for people who are not even playing.
/// </para>
/// <para>
/// With a Playit tunnel this socket is reachable from the internet, so everything it reads is
/// treated as hostile: lengths are bounded before anything is allocated, every connection has a
/// deadline, and the number of them at once is capped.
/// </para>
/// </summary>
public sealed class WakeOnDemandListener : IDisposable
{
    /// <summary>A handshake is tens of bytes; anything near this is not a real client.</summary>
    private const int MaxPacketBytes = 32 * 1024;

    /// <summary>Protocol maximum is 32767, but nothing we read legitimately comes close.</summary>
    private const int MaxStringChars = 512;

    private const int ConnectionTimeoutMs = 5000;

    /// <summary>Enough for a household; past this someone is not trying to play.</summary>
    private const int MaxConcurrentConnections = 16;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Func<WakeStatus>? _status;
    private Action? _onJoinAttempt;
    private int _open;

    /// <summary>Cached data URI of the server icon, so it isn't re-read on every ping.</summary>
    private string? _favicon;
    private string? _faviconSource;

    public bool IsListening => _listener is not null;

    /// <summary>
    /// Starts answering on <paramref name="port"/>. Returns false when the port can't be bound —
    /// the caller keeps working, it just means no wake-on-demand for now.
    /// </summary>
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
            _favicon = _faviconSource = null;
            _cts = new CancellationTokenSource();

            _ = AcceptLoopAsync(listener, _cts.Token);
            return true;
        }
        catch
        {
            // Port already taken (a leftover server, another app): nothing to do but skip it.
            Stop();
            return false;
        }
    }

    /// <summary>
    /// Releases the port. Must be called before the real server starts, or Java finds the port busy
    /// — and the app would offer to kill the process holding it, which would be this one.
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

            _ = Task.Run(() =>
            {
                try { Serve(client); }
                catch { /* malformed client: it just gets no answer */ }
                finally
                {
                    Interlocked.Decrement(ref _open);
                    try { client.Close(); } catch { /* already gone */ }
                }
            }, CancellationToken.None);
        }
    }

    private void Serve(TcpClient client)
    {
        client.ReceiveTimeout = client.SendTimeout = ConnectionTimeoutMs;
        using var stream = client.GetStream();

        var handshake = ReadPacket(stream);
        if (handshake is null) return;

        var (id, body) = handshake.Value;
        if (id != 0x00) return;

        // protocol version, address, port, next state
        var protocol = ReadVarInt(body);
        ReadString(body);                       // address the client dialled; not ours to trust
        if (body.Length - body.Position >= 2) body.Position += 2;   // port
        var nextState = ReadVarInt(body);

        var status = _status?.Invoke();
        if (status is null) return;

        if (nextState == 1) ServeStatus(stream, protocol, status);
        else if (nextState == 2) ServeLogin(stream, status);
    }

    private void ServeStatus(NetworkStream stream, int protocol, WakeStatus status)
    {
        // Status request (empty). Some clients send it, some go straight to the ping.
        var request = ReadPacket(stream);
        if (request is null) return;

        if (request.Value.Id == 0x00)
        {
            Send(stream, 0x00, w => WriteString(w, BuildStatusJson(status, protocol, Favicon(status.IconPath))));

            // The ping the client uses to draw a latency bar: echo its payload back unchanged.
            var ping = ReadPacket(stream);
            if (ping is { Id: 0x01 })
            {
                var payload = new byte[8];
                if (ping.Value.Body.Read(payload, 0, 8) == 8)
                    Send(stream, 0x01, w => w.Write(payload, 0, 8));
            }
        }
    }

    private void ServeLogin(NetworkStream stream, WakeStatus status)
    {
        // Waking first: the disconnect below closes the connection, and the point of the whole
        // exercise is that the server is already coming up by the time they read the message.
        _onJoinAttempt?.Invoke();

        Send(stream, 0x00, w => WriteString(w, JsonSerializer.Serialize(
            new Dictionary<string, object> { ["text"] = status.DisconnectMessage })));
        stream.Flush();
    }

    /// <summary>The server-list answer. Public shape so it can be checked without a Minecraft client.</summary>
    internal static string BuildStatusJson(WakeStatus status, int protocol, string? favicon)
    {
        var json = new Dictionary<string, object>
        {
            // The client's own protocol number, echoed back. Answering with a fixed one makes it
            // draw the server as incompatible and refuse to even try to join.
            ["version"] = new Dictionary<string, object>
            {
                ["name"] = status.VersionName,
                ["protocol"] = protocol
            },
            ["players"] = new Dictionary<string, object>
            {
                ["max"] = status.MaxPlayers,
                ["online"] = 0,
                ["sample"] = Array.Empty<object>()
            },
            ["description"] = new Dictionary<string, object> { ["text"] = status.Description }
        };
        if (favicon is not null) json["favicon"] = favicon;

        return JsonSerializer.Serialize(json);
    }

    /// <summary>The server's own icon as a data URI, read once and remembered.</summary>
    private string? Favicon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_faviconSource == path) return _favicon;

        _faviconSource = path;
        _favicon = null;
        try
        {
            var info = new FileInfo(path);
            // A 64x64 PNG is a couple of KB; anything much larger isn't the server icon.
            if (info.Exists && info.Length is > 0 and <= 128 * 1024)
                _favicon = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { /* unreadable icon: the client just shows the default one */ }

        return _favicon;
    }

    // --- protocol plumbing ---------------------------------------------------------------------

    /// <summary>Reads one length-prefixed packet, or null if it is absent, truncated or absurd.</summary>
    private static (int Id, MemoryStream Body)? ReadPacket(Stream stream)
    {
        var length = TryReadVarInt(stream);
        if (length is null or <= 0 or > MaxPacketBytes) return null;

        var buffer = new byte[length.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return null;
            read += n;
        }

        var body = new MemoryStream(buffer, writable: false);
        return (ReadVarInt(body), body);
    }

    private static void Send(Stream stream, int id, Action<Stream> writeBody)
    {
        using var payload = new MemoryStream();
        WriteVarInt(payload, id);
        writeBody(payload);

        using var packet = new MemoryStream();
        WriteVarInt(packet, (int)payload.Length);
        payload.Position = 0;
        payload.CopyTo(packet);

        var bytes = packet.ToArray();
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static void WriteVarInt(Stream stream, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (v != 0);
    }

    internal static int ReadVarInt(Stream stream) => TryReadVarInt(stream) ?? 0;

    /// <summary>VarInt, capped at five bytes so a hostile stream can't spin here forever.</summary>
    internal static int? TryReadVarInt(Stream stream)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = stream.ReadByte();
            if (b < 0) return null;

            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        return null;
    }

    internal static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Reads a length-prefixed UTF-8 string, refusing to allocate for a bogus length.</summary>
    internal static string ReadString(Stream stream)
    {
        var length = TryReadVarInt(stream);
        if (length is null or < 0 or > MaxStringChars * 4) return string.Empty;

        var buffer = new byte[length.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return string.Empty;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer);
    }
}
