using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Finds the port a Minecraft Java address really lives on, the way the game client does.
/// </summary>
/// <remarks>
/// <para>
/// A playit tunnel hands out a bare domain — <c>jakarta-rivers.tun.ply.gg</c>, no port — and the
/// port is published separately as an SRV record. Looked up on a real one, that record says
/// <c>_minecraft._tcp.jakarta-rivers.tun.ply.gg → 14444</c>. The game resolves it; a plain
/// <c>TcpClient</c> does not, so pinging the domain on 25565 reaches nothing at all — which is how
/// the panel came to report a perfectly healthy tunnel as dead.
/// </para>
/// <para>
/// Written by hand rather than adding a DNS library for one record type. It is a fixed-shape
/// question and a fixed-shape answer, and the parsing is where the danger is, so it is separated
/// from the socket and tested against captured bytes.
/// </para>
/// </remarks>
public static class MinecraftSrv
{
    /// <summary>The name Minecraft asks for.</summary>
    public static string QueryName(string host) => "_minecraft._tcp." + host.Trim().TrimEnd('.');

    /// <summary>
    /// The port for <paramref name="host"/>, or null when there is no SRV record.
    /// </summary>
    /// <remarks>
    /// Null means "ask somewhere else", not "broken": most addresses have no SRV record and are
    /// simply reached on the port they were given. Never throws — a name server that is slow, absent
    /// or hostile must not be able to take down the panel that called this.
    /// </remarks>
    public static async Task<int?> LookupPortAsync(string? host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        try
        {
            var server = FirstNameServer();
            if (server is null) return null;

            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53);

            var query = BuildQuery(QueryName(host!), id: 0x4D43);
            await udp.SendAsync(query, ct);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));

            var reply = await udp.ReceiveAsync(deadline.Token);
            return ReadSrvPort(reply.Buffer);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The machine's own resolver, whichever interface has one.</summary>
    private static IPAddress? FirstNameServer() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .FirstOrDefault(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);

    /// <summary>Builds a single SRV question.</summary>
    internal static byte[] BuildQuery(string name, ushort id)
    {
        var packet = new List<byte>
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00,             // standard query, recursion desired
            0x00, 0x01,             // one question
            0x00, 0x00,             // no answers
            0x00, 0x00,             // no authority
            0x00, 0x00,             // no additional
        };

        // A name goes on the wire as length-prefixed labels, ending with a zero length.
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) return Array.Empty<byte>();
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }

        packet.Add(0x00);
        packet.AddRange(new byte[] { 0x00, 0x21 });   // type SRV
        packet.AddRange(new byte[] { 0x00, 0x01 });   // class IN
        return packet.ToArray();
    }

    /// <summary>
    /// Pulls the port out of the first SRV answer, or null.
    /// </summary>
    /// <remarks>
    /// Every length read is bounds-checked. This parses a reply from the network, and the reply can
    /// be anything at all — including a deliberately malformed one, since a name server is
    /// reachable by whoever is on the way to it.
    /// </remarks>
    internal static int? ReadSrvPort(byte[] reply)
    {
        if (reply.Length < 12) return null;

        var answers = (reply[6] << 8) | reply[7];
        if (answers <= 0) return null;

        var questions = (reply[4] << 8) | reply[5];
        var at = 12;

        for (var q = 0; q < questions; q++)
        {
            if (!SkipName(reply, ref at)) return null;
            at += 4;                                  // type and class
            if (at > reply.Length) return null;
        }

        for (var a = 0; a < answers; a++)
        {
            if (!SkipName(reply, ref at)) return null;
            if (at + 10 > reply.Length) return null;

            var type = (reply[at] << 8) | reply[at + 1];
            var dataLength = (reply[at + 8] << 8) | reply[at + 9];
            at += 10;

            if (at + dataLength > reply.Length) return null;

            // SRV data is priority, weight, port, target. The port is the third pair.
            if (type == 33 && dataLength >= 6)
                return (reply[at + 4] << 8) | reply[at + 5];

            at += dataLength;
        }

        return null;
    }

    /// <summary>Steps over a name, following the one level of compression a reply may use.</summary>
    private static bool SkipName(byte[] data, ref int at)
    {
        while (true)
        {
            if (at >= data.Length) return false;

            var length = data[at];
            if (length == 0) { at++; return true; }

            // Two high bits set means a pointer, which is always the end of the name.
            if ((length & 0xC0) == 0xC0) { at += 2; return at <= data.Length; }

            at += 1 + length;
            if (at > data.Length) return false;
        }
    }
}
