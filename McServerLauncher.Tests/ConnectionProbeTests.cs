using System.Net;
using System.Net.Sockets;
using System.Text;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The measurements behind the connection test, against servers that live inside the test.
/// </summary>
/// <remarks>
/// Nothing here reaches the internet: the Java and Bedrock servers are a few lines each on the
/// loopback, answering the same packets the real ones do. The speed test and the DNS lookup are
/// not exercised — only what they read back is (<see cref="ConnectionProbe.ParseSrv"/>).
/// </remarks>
public class ConnectionProbeTests
{
    [Fact]
    public void TheStatsAreWhatTheSamplesSay()
    {
        var stats = new LatencyStats([10, 20, 30, 20], Sent: 5);

        Assert.Equal(10, stats.Min);
        Assert.Equal(30, stats.Max);
        Assert.Equal(20, stats.Average);
        Assert.Equal(10, stats.Jitter);          // |10-20|, |20-30|, |30-20|
        Assert.Equal(0.2, stats.Loss, 3);
    }

    [Fact]
    public void NothingBackIsAllLost()
    {
        var stats = new LatencyStats([], Sent: 8);

        Assert.False(stats.AnyReply);
        Assert.Equal(1, stats.Loss);
        Assert.Equal(StepState.Bad, ConnectionTestViewModel.Grade(stats, 30, 100));
    }

    [Theory]
    [InlineData(10, 0, StepState.Good)]
    [InlineData(10, 1, StepState.Fair)]   // fast, but a packet went missing
    [InlineData(60, 0, StepState.Fair)]
    [InlineData(150, 0, StepState.Bad)]
    [InlineData(10, 3, StepState.Bad)]    // more than a quarter lost
    public void AStretchIsGradedByItsAverageAndItsLoss(double ms, int lost, StepState expected)
    {
        var samples = Enumerable.Repeat(ms, 8 - lost).ToList();
        Assert.Equal(expected, ConnectionTestViewModel.Grade(new LatencyStats(samples, 8), good: 30, fair: 100));
    }

    [Fact]
    public void AnSrvAnswerGivesTheRealHostAndPort()
    {
        const string json = """
            {"Status":0,"Answer":[{"name":"_minecraft._tcp.x.gl.joinmc.link.","type":33,"TTL":60,
              "data":"0 5 41234 147-185-221-17.example.net."}]}
            """;

        Assert.Equal(("147-185-221-17.example.net", 41234), ConnectionProbe.ParseSrv(json));
        Assert.Null(ConnectionProbe.ParseSrv("""{"Status":3}"""));
    }

    [Fact]
    public async Task AnAddressWithItsPortIsTakenAsWritten() =>
        Assert.Equal(("play.example.org", 25570), await ConnectionProbe.ResolveJavaAsync("play.example.org:25570"));

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(-1)]
    public void AVarIntReadsBackWhatWasWritten(int value)
    {
        var stream = new MemoryStream();
        ConnectionProbe.WriteVarInt(stream, value);
        stream.Position = 0;
        Assert.Equal(value, ConnectionProbe.ReadVarInt(stream));
    }

    [Fact]
    public async Task AJavaServerAnswersTheStatusPingWithItsVersionAndPlayers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeOneStatusAsync(listener);

        var reply = await ConnectionProbe.PingJavaAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
        await serving;

        Assert.NotNull(reply);
        Assert.Equal(767, reply.Protocol);
        Assert.Equal("1.21.1", reply.Version);
        Assert.Equal(3, reply.Online);
        Assert.Equal(20, reply.Max);
        Assert.True(reply.LatencyMs >= 0);
    }

    [Fact]
    public async Task NobodyListeningIsNoAnswer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Null(await ConnectionProbe.PingJavaAsync("127.0.0.1", port, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ABedrockServerAnswersTheUnconnectedPing()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var serving = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync();
            Assert.Equal(0x01, request.Buffer[0]);
            var pong = new byte[35];
            pong[0] = 0x1c;
            await server.SendAsync(pong, request.RemoteEndPoint);
        });

        var ms = await ConnectionProbe.PingBedrockAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
        await serving;

        Assert.NotNull(ms);
    }

    /// <summary>Answers one status ping the way a Minecraft server does.</summary>
    private static async Task ServeOneStatusAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();

        await ReadPacketAsync(stream);           // handshake
        await ReadPacketAsync(stream);           // status request

        var json = Encoding.UTF8.GetBytes(
            """{"version":{"name":"1.21.1","protocol":767},"players":{"max":20,"online":3},"description":"x"}""");
        var body = new MemoryStream();
        ConnectionProbe.WriteVarInt(body, 0x00);
        ConnectionProbe.WriteVarInt(body, json.Length);
        body.Write(json);
        await WritePacketAsync(stream, body.ToArray());

        var ping = await ReadPacketAsync(stream);
        await WritePacketAsync(stream, ping);    // the pong echoes the ping
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream)
    {
        int length = 0, shift = 0;
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one);
            length |= (one[0] & 0x7f) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
        }
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload)
    {
        var framed = new MemoryStream();
        ConnectionProbe.WriteVarInt(framed, payload.Length);
        framed.Write(payload);
        await stream.WriteAsync(framed.ToArray());
    }
}
