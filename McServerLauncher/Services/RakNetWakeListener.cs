using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace McServerLauncher.Services;

/// <summary>
/// Answers Bedrock clients that speak RakNet on Geyser's UDP port while the real server is stopped,
/// so they see a sleeping server in their list and wake it by trying to join.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NetherNetWakeListener"/> covers the client that opens with HTTP over TCP (Bedrock 26.60
/// onwards). Every other one — an older client, and whichever platforms still use it — speaks RakNet
/// over UDP, and with nothing on that port the server showed as offline and could not be woken at
/// all. This holds the UDP side of the same port number.
/// </para>
/// <para>
/// It speaks the two offline messages of RakNet and nothing else. An <em>unconnected ping</em> is
/// what the client sends to draw the server in its list: it gets a pong with the server's name and
/// the "asleep" notice, and wakes nothing — a list being open is not somebody playing, which is the
/// same line the Java listener draws between a status request and a login. An <em>open connection
/// request</em> is what the client sends when Join is pressed, and that is what wakes the server. It
/// is not answered: there is no way to say "starting, try again" at this level, so the client gives
/// up after a few seconds and the player joins once Geyser is up.
/// </para>
/// <para>
/// With a Playit tunnel this socket is reachable from the internet, and UDP has a problem TCP does
/// not: the sender's address can be forged, so anything that answers is a way to throw packets at a
/// third party. The pong is therefore kept small and rationed (<see cref="MaxPongsPerSecond"/>); a
/// join request must carry RakNet's magic and the padding a real client sends, which a forged
/// one-byte packet does not; and the wake itself is rationed too.
/// </para>
/// </remarks>
public sealed class RakNetWakeListener : IDisposable
{
    internal const byte UnconnectedPing = 0x01;
    internal const byte UnconnectedPingOpenConnections = 0x02;
    internal const byte OpenConnectionRequest1 = 0x05;
    internal const byte UnconnectedPong = 0x1c;

    /// <summary>The sixteen bytes every offline RakNet message carries.</summary>
    internal static readonly byte[] Magic =
        [0x00, 0xff, 0xff, 0x00, 0xfe, 0xfe, 0xfe, 0xfe, 0xfd, 0xfd, 0xfd, 0xfd, 0x12, 0x34, 0x56, 0x78];

    /// <summary>
    /// The shortest join request taken for one. A client pads it out to the packet size it is
    /// probing, the smallest of which is 576 bytes on the wire; this leaves room for the headers.
    /// </summary>
    internal const int MinJoinRequestBytes = 400;

    /// <summary>More pings than this in a second are not a household looking at its server list.</summary>
    internal const int MaxPongsPerSecond = 20;

    /// <summary>A client repeats its join request several times a second; one wake is enough.</summary>
    internal static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(5);

    /// <summary>Bedrock's name field is short; anything longer is cut by the client anyway.</summary>
    private const int MaxNameChars = 80;

    private static readonly Regex ColourCodes = new(((char)0xA7) + ".", RegexOptions.Compiled);   // the section sign: a Minecraft colour code

    private readonly long _guid = Random.Shared.NextInt64();
    private readonly object _gate = new();
    private Socket? _socket;
    private Thread? _thread;
    private Func<WakeStatus>? _status;
    private Action? _onJoinAttempt;
    private int _port;

    private long _pongWindowStart;
    private int _pongsInWindow;
    private long _lastWakeTicks = long.MinValue;

    public bool IsListening => _socket is not null;

    /// <summary>
    /// Starts answering on <paramref name="port"/>. Returns false when the port can't be bound —
    /// the caller keeps working, it just means no RakNet wake-on-demand for now.
    /// </summary>
    /// <param name="port">Geyser's Bedrock port; the UDP side of it.</param>
    /// <param name="status">What the client is told about the server, asked for on every ping.</param>
    /// <param name="onJoinAttempt">Somebody tried to join. Called on the listener's thread.</param>
    public bool Start(int port, Func<WakeStatus> status, Action onJoinAttempt)
    {
        Stop();

        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IgnoreUnreachableReplies(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException)
        {
            return false;   // Geyser left running, another app: nothing to do but skip it
        }

        lock (_gate)
        {
            _status = status;
            _onJoinAttempt = onJoinAttempt;
            _port = port;
            _socket = socket;
            _lastWakeTicks = long.MinValue;
        }

        _thread = new Thread(() => Receive(socket)) { IsBackground = true, Name = "RakNet wake listener" };
        _thread.Start();
        return true;
    }

    /// <summary>
    /// Releases the port. Must be called before the real server starts, or Geyser cannot bind it.
    /// </summary>
    public void Stop()
    {
        Socket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
            _status = null;
            _onJoinAttempt = null;
        }
        if (socket is null) return;

        try { socket.Close(); } catch { /* already gone */ }

        // The port is only free once the receive has let go of it, and the caller starts Geyser
        // next. Never from the listener's own thread: a wake that stops it would wait for itself.
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// On Windows a UDP socket that sent to a closed port gets the ICMP refusal back as an error on
    /// its next receive, which would end the loop the first time a client went away.
    /// </summary>
    private static void IgnoreUnreachableReplies(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;

        const int SioUdpConnReset = -1744830452;
        try { socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
        catch (SocketException) { /* an older system: the loop also survives the error itself */ }
    }

    private void Receive(Socket socket)
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            int length;
            try
            {
                length = socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode
                                                is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;   // one client's problem, not the listener's
            }
            catch (Exception) { return; }   // closed: the server is starting or the app is going

            try
            {
                var reply = Handle(buffer.AsSpan(0, length));
                if (reply is not null) socket.SendTo(reply, from);
            }
            catch (Exception)
            {
                // A reply that could not be sent, a status that could not be built: this client
                // gets nothing, the next one is served.
            }
        }
    }

    /// <summary>
    /// What to answer to one datagram, or null for nothing. Wakes the server on a join request.
    /// </summary>
    internal byte[]? Handle(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0) return null;

        switch (packet[0])
        {
            case UnconnectedPing or UnconnectedPingOpenConnections:
                // id, the client's clock (8), the magic (16), the client's id (8, not always sent)
                if (packet.Length < 25 || !packet.Slice(9, 16).SequenceEqual(Magic)) return null;
                if (!MayPong()) return null;

                Func<WakeStatus>? status;
                int port;
                lock (_gate) { status = _status; port = _port; }
                return status?.Invoke() is { } s
                    ? BuildPong(packet.Slice(1, 8), _guid, s, port)
                    : null;

            case OpenConnectionRequest1:
                // id, the magic (16), the RakNet version (1), then zeros up to the size being probed
                if (packet.Length < MinJoinRequestBytes || !packet.Slice(1, 16).SequenceEqual(Magic))
                    return null;
                if (!MayWake()) return null;

                Action? wake;
                lock (_gate) wake = _onJoinAttempt;
                wake?.Invoke();
                return null;

            default:
                return null;
        }
    }

    private bool MayPong()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (now - _pongWindowStart >= 1000)
            {
                _pongWindowStart = now;
                _pongsInWindow = 0;
            }
            return ++_pongsInWindow <= MaxPongsPerSecond;
        }
    }

    private bool MayWake()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastWakeTicks != long.MinValue && now - _lastWakeTicks < WakeInterval.TotalMilliseconds)
                return false;

            _lastWakeTicks = now;
            return true;
        }
    }

    /// <summary>The pong a Bedrock client draws its server list from. Public shape so it can be tested.</summary>
    internal static byte[] BuildPong(ReadOnlySpan<byte> clientTime, long guid, WakeStatus status, int port)
    {
        var text = Encoding.UTF8.GetBytes(BuildAdvertisement(status, guid, port));

        var pong = new byte[1 + 8 + 8 + 16 + 2 + text.Length];
        pong[0] = UnconnectedPong;
        clientTime.CopyTo(pong.AsSpan(1));
        BinaryPrimitives.WriteInt64BigEndian(pong.AsSpan(9), guid);
        Magic.CopyTo(pong, 17);
        BinaryPrimitives.WriteUInt16BigEndian(pong.AsSpan(33), (ushort)text.Length);
        text.CopyTo(pong, 35);
        return pong;
    }

    /// <summary>
    /// The semicolon-separated line inside the pong: edition, name, protocol, version, players,
    /// limit, server id, second line, game mode, and the ports.
    /// </summary>
    /// <remarks>
    /// The notice goes on the first line, next to the server's name, because that is the only line
    /// the Bedrock list shows for a server that is not on the local network. Semicolons would be
    /// read as field separators and colour codes as literal characters, so both are taken out.
    /// </remarks>
    internal static string BuildAdvertisement(WakeStatus status, long guid, int port)
    {
        var lines = status.Description.Split('\n');
        var name = Clean(lines[0]);
        var notice = lines.Length > 1 ? Clean(lines[1]) : string.Empty;

        var title = notice.Length == 0 ? name : name.Length == 0 ? notice : $"{name} - {notice}";
        if (title.Length > MaxNameChars) title = title[..MaxNameChars];

        return string.Join(';', "MCPE", title, NetherNetWakeListener.ProtocolNumber,
            Clean(status.VersionName), 0, status.MaxPlayers, guid, notice, "Survival", 1, port, port) + ";";
    }

    private static string Clean(string text) =>
        ColourCodes.Replace(text, string.Empty).Replace(';', ',').Replace("\r", string.Empty).Trim();
}
