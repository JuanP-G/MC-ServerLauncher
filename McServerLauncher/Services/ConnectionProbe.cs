using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace McServerLauncher.Services;

/// <summary>Round trips to one place: what came back, out of how many were sent.</summary>
public sealed record LatencyStats(IReadOnlyList<double> Samples, int Sent)
{
    public static readonly LatencyStats None = new(Array.Empty<double>(), 0);

    public int Received => Samples.Count;
    public bool AnyReply => Samples.Count > 0;
    public double Min => AnyReply ? Samples.Min() : 0;
    public double Max => AnyReply ? Samples.Max() : 0;
    public double Average => AnyReply ? Samples.Average() : 0;

    /// <summary>How much one round trip differs from the next, on average: what players feel as stutter.</summary>
    public double Jitter =>
        Samples.Count < 2 ? 0 : Samples.Zip(Samples.Skip(1), (a, b) => Math.Abs(a - b)).Average();

    /// <summary>The share that never came back, 0–1.</summary>
    public double Loss => Sent == 0 ? 0 : 1 - Received / (double)Sent;
}

/// <summary>What a Minecraft server said to a status ping.</summary>
public sealed record SlpReply(double LatencyMs, int Protocol, string? Version, int Online, int Max);

/// <summary>How fast bytes went one way.</summary>
/// <param name="Mbps">Megabits per second, over the whole transfer.</param>
/// <param name="PeakBytesPerSecond">The best quarter of a second, in bytes per second.</param>
/// <param name="Bytes">How many bytes went, in all.</param>
/// <param name="Elapsed">How long they took.</param>
public sealed record Throughput(double Mbps, double PeakBytesPerSecond, long Bytes, TimeSpan Elapsed)
{
    public static readonly Throughput None = new(0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// The measurements behind the connection test: a Minecraft status ping, a Bedrock (RakNet) ping, a
/// bare TCP round trip, and a download and an upload to time the line.
/// </summary>
/// <remarks>
/// <para>
/// Each tells apart one stretch of the way a player's packets travel. The status ping to
/// <c>127.0.0.1</c> is the server alone; the TCP round trip to Cloudflare is this machine's internet;
/// the status ping to the tunnel's public address goes out to Playit and back in through the agent,
/// which is the path a player's packets take. Comparing them says where the time goes.
/// </para>
/// <para>
/// The status ping is the one the game's server list sends (handshake with next state 1, status
/// request, then a ping whose echo is timed), so any server that shows up in the list answers it,
/// whatever its type. Only the echo is timed — the connection and the status JSON are not, as the
/// game itself does.
/// </para>
/// <para>
/// The speed test uses Cloudflare's public endpoints (<c>speed.cloudflare.com</c>, the ones its own
/// web test calls). No account and no key; what is sent up is random bytes.
/// </para>
/// </remarks>
public static class ConnectionProbe
{
    public const string SpeedHost = "speed.cloudflare.com";
    public const string LatencyHost = "1.1.1.1";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // ---------------------------------------------------------------- sampling

    /// <summary>Runs <paramref name="probe"/> <paramref name="count"/> times, a little apart.</summary>
    public static async Task<LatencyStats> SampleAsync(
        Func<CancellationToken, Task<double?>> probe, int count, TimeSpan gap,
        IProgress<double>? each = null, CancellationToken ct = default)
    {
        var samples = new List<double>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ms = await probe(ct);
            if (ms is { } got)
            {
                samples.Add(got);
                each?.Report(got);
            }
            if (i < count - 1) await Task.Delay(gap, ct);
        }
        return new LatencyStats(samples, count);
    }

    // ---------------------------------------------------------------- Java

    /// <summary>
    /// A protocol number to put in the handshake when the server's own is not known: a real one,
    /// recent (1.21.1). See <see cref="PingJavaAsync"/> for why -1 will not do.
    /// </summary>
    public const int FallbackProtocol = 767;

    /// <summary>A server-list ping. Null when nothing answered in time.</summary>
    /// <param name="host">
    /// Where to connect, and what the handshake says it connected to. A name, not an IP, when going
    /// through Playit: its edge routes by it, and an IP there gets the connection reset.
    /// </param>
    /// <param name="port">The port to connect to, also written into the handshake.</param>
    /// <param name="timeout">For the whole exchange: connecting, the status and the ping.</param>
    /// <param name="ct">Stops it early; a cancellation is not reported as "no answer".</param>
    /// <param name="protocol">
    /// The protocol number in the handshake. Never -1, the "whatever you speak" that status tools
    /// send: Playit's Minecraft edge reads the handshake before passing it on and resets a connection
    /// whose protocol is not a real one (found on 2026-10-10 against a live tunnel: -1 was reset every
    /// time within a few milliseconds, 776 and 767 answered every time), and a server asked with -1
    /// may report -1 as its own protocol, which is no use either. Asked with a real number, a server
    /// answers with its own, so the local ping finds out what the tunnel ping then sends.
    /// </param>
    public static async Task<SlpReply?> PingJavaAsync(string host, int port, TimeSpan timeout,
        CancellationToken ct = default, int protocol = FallbackProtocol)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, cts.Token);
            var stream = client.GetStream();

            // Handshake: the protocol (see above), the address and port as typed, next state 1
            // (status). Then the status request.
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, protocol);
            WriteString(handshake, host);
            var portBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
            handshake.Write(portBytes);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), cts.Token);
            await WritePacketAsync(stream, [0x00], cts.Token);

            var status = await ReadPacketAsync(stream, cts.Token);
            var reader = new MemoryStream(status);
            if (ReadVarInt(reader) != 0x00) return null;
            var json = ReadString(reader);

            var ping = new byte[9];
            ping[0] = 0x01;
            BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), Environment.TickCount64);
            var watch = Stopwatch.StartNew();
            await WritePacketAsync(stream, ping, cts.Token);
            await ReadPacketAsync(stream, cts.Token);
            var ms = watch.Elapsed.TotalMilliseconds;

            var (spoken, version, online, max) = ReadStatus(json);
            return new SlpReply(ms, spoken, version, online, max);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static (int Protocol, string? Version, int Online, int Max) ReadStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int protocol = 0, online = 0, max = 0;
            string? version = null;
            if (root.TryGetProperty("version", out var v))
            {
                if (v.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var n)) protocol = n;
                if (v.TryGetProperty("name", out var name)) version = name.GetString();
            }
            if (root.TryGetProperty("players", out var players))
            {
                if (players.TryGetProperty("online", out var o) && o.TryGetInt32(out var on)) online = on;
                if (players.TryGetProperty("max", out var m) && m.TryGetInt32(out var mx)) max = mx;
            }
            return (protocol, version, online, max);
        }
        catch (JsonException)
        {
            return (0, null, 0, 0);
        }
    }

    /// <summary>
    /// Where a Java address really points: <c>host:port</c> as written, or the
    /// <c>_minecraft._tcp</c> SRV record, which is how Playit's domains carry their port.
    /// </summary>
    /// <remarks>
    /// .NET has no SRV lookup of its own, so it is asked of Cloudflare's DNS over HTTPS — the same
    /// provider the speed test already talks to. Without an answer the game's default port is what
    /// a client would try, so it is what is tried here.
    /// </remarks>
    public static async Task<(string Host, int Port)> ResolveJavaAsync(string address, CancellationToken ct = default)
    {
        var text = address.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var written)) return (text[..colon], written);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://cloudflare-dns.com/dns-query?name=_minecraft._tcp.{Uri.EscapeDataString(text)}&type=SRV");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http.SendAsync(request, cts.Token);
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            if (ParseSrv(json) is { } srv) return srv;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // No answer is no SRV record: fall through to the default port.
        }
        return (text, 25565);
    }

    /// <summary>The target of the first SRV answer in a DNS-over-HTTPS JSON reply.</summary>
    internal static (string Host, int Port)? ParseSrv(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return null;
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 33) continue;
                // "priority weight port target."
                var parts = answer.GetProperty("data").GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts is { Length: 4 } && int.TryParse(parts[2], out var port))
                    return (parts[3].TrimEnd('.'), port);
            }
        }
        catch (Exception) { /* not the reply expected */ }
        return null;
    }

    // ---------------------------------------------------------------- Bedrock

    private static readonly byte[] RakNetMagic =
        [0x00, 0xff, 0xff, 0x00, 0xfe, 0xfe, 0xfe, 0xfe, 0xfd, 0xfd, 0xfd, 0xfd, 0x12, 0x34, 0x56, 0x78];

    /// <summary>
    /// A RakNet unconnected ping, which is what the Bedrock client sends to fill its server list.
    /// Milliseconds, or null when nothing answered in time.
    /// </summary>
    public static async Task<double?> PingBedrockAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var udp = new UdpClient();
            udp.Connect(host, port);
            var packet = new byte[33];
            packet[0] = 0x01;
            BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1), Environment.TickCount64);
            RakNetMagic.CopyTo(packet, 9);
            BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(25), Random.Shared.NextInt64());

            var watch = Stopwatch.StartNew();
            await udp.SendAsync(packet, cts.Token);
            while (true)
            {
                var reply = await udp.ReceiveAsync(cts.Token);
                if (reply.Buffer.Length > 0 && reply.Buffer[0] == 0x1c) return watch.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the line

    /// <summary>How long a TCP connection takes to open: one round trip, and nothing else.</summary>
    public static async Task<double?> TcpRoundTripAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, cts.Token);
            return watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Downloads up to <paramref name="bytes"/>, for at most <paramref name="limit"/>.</summary>
    public static async Task<Throughput> DownloadAsync(long bytes, TimeSpan limit, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        var meter = new Meter();
        try
        {
            using var response = await Http.GetAsync($"https://{SpeedHost}/__down?bytes={bytes}",
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[64 * 1024];
            meter.Start();
            int read;
            while ((read = await body.ReadAsync(buffer, cts.Token)) > 0) meter.Add(read);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The time ran out, or the line failed: what arrived until then is the measurement,
            // and nothing at all reads as none.
        }
        return meter.Result();
    }

    /// <summary>Uploads up to <paramref name="bytes"/> of random data, for at most <paramref name="limit"/>.</summary>
    public static async Task<Throughput> UploadAsync(long bytes, TimeSpan limit, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        var meter = new Meter();
        try
        {
            using var content = new MeteredContent(bytes, meter);
            using var response = await Http.PostAsync($"https://{SpeedHost}/__up", content, cts.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The time ran out, or the line failed: what went up until then is the measurement.
        }
        return meter.Result();
    }

    /// <summary>Counts bytes as they go, and remembers the best quarter of a second.</summary>
    private sealed class Meter
    {
        private readonly Stopwatch _watch = new();
        private long _windowStartBytes;
        private TimeSpan _windowStart;
        private double _peak;

        public long Bytes { get; private set; }

        public void Start()
        {
            if (!_watch.IsRunning) _watch.Start();
        }

        public void Add(int count)
        {
            Start();
            Bytes += count;
            var now = _watch.Elapsed;
            var span = now - _windowStart;
            if (span < TimeSpan.FromMilliseconds(250)) return;
            _peak = Math.Max(_peak, (Bytes - _windowStartBytes) / span.TotalSeconds);
            (_windowStart, _windowStartBytes) = (now, Bytes);
        }

        public Throughput Result()
        {
            var elapsed = _watch.Elapsed;
            if (Bytes == 0 || elapsed <= TimeSpan.Zero) return Throughput.None;
            var average = Bytes / elapsed.TotalSeconds;
            return new Throughput(average * 8 / 1_000_000, Math.Max(_peak, average), Bytes, elapsed);
        }
    }

    /// <summary>A body of random bytes that reports each chunk to the meter as it is sent.</summary>
    private sealed class MeteredContent(long length, Meter meter) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            Random.Shared.NextBytes(chunk);
            meter.Start();
            for (long sent = 0; sent < length;)
            {
                var n = (int)Math.Min(chunk.Length, length - sent);
                await stream.WriteAsync(chunk.AsMemory(0, n));
                sent += n;
                meter.Add(n);
            }
        }

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }

    // ---------------------------------------------------------------- wire format

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var framed = new MemoryStream();
        WriteVarInt(framed, payload.Length);
        framed.Write(payload);
        await stream.WriteAsync(framed.ToArray(), ct);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        if (length is < 0 or > 1 << 21) throw new InvalidDataException("packet length");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    internal static void WriteVarInt(Stream stream, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7f);
            v >>= 7;
            if (v != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (v != 0);
    }

    internal static int ReadVarInt(Stream stream)
    {
        int value = 0, shift = 0;
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            value |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("varint");
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        int value = 0, shift = 0;
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, ct);
            value |= (one[0] & 0x7f) << shift;
            if ((one[0] & 0x80) == 0) return value;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("varint");
        }
    }

    private static void WriteString(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static string ReadString(Stream stream)
    {
        var length = ReadVarInt(stream);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }
}
