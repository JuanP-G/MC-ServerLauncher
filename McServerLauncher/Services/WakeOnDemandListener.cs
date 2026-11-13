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
/// <param name="DisconnectMessage">Shown full-screen to whoever presses Join and wakes it.</param>
/// <param name="RefusedMessage">
/// Shown instead to somebody who may not wake it (see <see cref="WakePolicy"/>). Null falls back to
/// <paramref name="DisconnectMessage"/>.
/// </param>
public record WakeStatus(
    string Description,
    string VersionName,
    int MaxPlayers,
    string? IconPath,
    string DisconnectMessage,
    string? RefusedMessage = null);

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

    private readonly BoundedTcpListener _tcp;
    private Func<WakeStatus>? _status;
    private Func<string?, bool>? _onJoinAttempt;

    public WakeOnDemandListener() => _tcp = new BoundedTcpListener(Serve);

    /// <summary>
    /// The deadline actually applied. Settable so a test can exercise it in seconds instead of
    /// waiting out the real one; production never touches it.
    /// </summary>
    internal TimeSpan ConnectionDeadline
    {
        get => _tcp.ConnectionDeadline;
        set => _tcp.ConnectionDeadline = value;
    }

    /// <summary>Cached data URI of the server icon, so it isn't re-read on every ping.</summary>
    private string? _favicon;
    private string? _faviconSource;

    public bool IsListening => _tcp.IsListening;

    /// <summary>
    /// Starts answering on <paramref name="port"/>. Returns false when the port can't be bound —
    /// the caller keeps working, it just means no wake-on-demand for now.
    /// </summary>
    /// <param name="port">The server's own port.</param>
    /// <param name="status">What the server list shows, asked for on every ping.</param>
    /// <param name="onJoinAttempt">
    /// Somebody pressed Join, with the name they logged in as (null if the client sent none).
    /// Returns whether the server is being woken for them, which decides what they are told.
    /// Called on the connection's thread.
    /// </param>
    public bool Start(int port, Func<WakeStatus> status, Func<string?, bool> onJoinAttempt)
    {
        Stop();

        // Before the first connection can arrive, which is the moment Start is called on the socket.
        _status = status;
        _onJoinAttempt = onJoinAttempt;
        _favicon = _faviconSource = null;
        if (_tcp.Start(port)) return true;

        // Port already taken (a leftover server, another app): nothing to do but skip it.
        Stop();
        return false;
    }

    /// <summary>
    /// Releases the port. Must be called before the real server starts, or Java finds the port busy
    /// — and the app would offer to kill the process holding it, which would be this one.
    /// </summary>
    public void Stop()
    {
        _tcp.Stop();
        _status = null;
        _onJoinAttempt = null;
    }

    public void Dispose() => Stop();

    // The cap, the timeouts and the deadline are BoundedTcpListener's: this only speaks the protocol.
    private void Serve(NetworkStream stream)
    {
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

        // 2 is a login. 3 is a transfer (1.20.5 onwards): a player another server sent here with
        // /transfer, who logs in the same way and used to get no answer at all from a sleeping one.
        if (nextState == 1) ServeStatus(stream, protocol, status);
        else if (nextState is 2 or 3) ServeLogin(stream, status, ReadLoginName(stream));
    }

    private void ServeStatus(NetworkStream stream, int protocol, WakeStatus status)
    {
        // Status request (empty). Some clients send it, some go straight to the ping.
        var request = ReadPacket(stream);
        if (request is null) return;

        if (request.Value.Id == 0x00)
        {
            Send(stream, 0x00, w => WriteString(w,
                BuildStatusJson(status, protocol, Favicon(status.IconPath))));

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

    private void ServeLogin(NetworkStream stream, WakeStatus status, string? player)
    {
        // Waking first: the disconnect below closes the connection, and the point of the whole
        // exercise is that the server is already coming up by the time they read the message.
        var waking = _onJoinAttempt?.Invoke(player) ?? false;

        var message = waking ? status.DisconnectMessage : status.RefusedMessage ?? status.DisconnectMessage;
        Send(stream, 0x00, w => WriteString(w, JsonSerializer.Serialize(
            new Dictionary<string, object> { ["text"] = message })));
        stream.Flush();
    }

    /// <summary>
    /// The name in the client's Login Start, which follows the handshake; null if it did not send one.
    /// </summary>
    /// <remarks>
    /// The name has been the first field of Login Start in every version: what came after it (a
    /// signature, a UUID) changed, and none of it is needed here. A client that sends nothing, or
    /// something else, simply has no name — which only matters to a server that wakes for its
    /// whitelist alone.
    /// </remarks>
    internal static string? ReadLoginName(Stream stream)
    {
        try
        {
            var packet = ReadPacket(stream);
            if (packet is not { Id: 0x00 } login) return null;

            var name = ReadString(login.Body);
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;   // a deadline or a dropped connection: no name to go on
        }
    }

    /// <summary>
    /// The server-list answer. Public shape so it can be checked without a Minecraft client.
    /// </summary>
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
