using System.Net;
using System.Net.Sockets;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Timing a Minecraft server's answer, and deciding whose fault a slow one is.
/// </summary>
/// <remarks>
/// The protocol half is checked against a real socket rather than a mock, for the same reason
/// <see cref="WakeProtocolTests"/> does it: the question is whether the bytes on the wire are the
/// ones a Minecraft server expects, and a stand-in that agrees with our own writer would agree with
/// it whether or not the writer is right. The listener under test here is the app's own
/// <see cref="WakeOnDemandListener"/> — so these two also prove the two halves still understand
/// each other, which is the thing that would quietly break if either drifted.
/// </remarks>
public class ServerPingTests
{
    /// <summary>Runs a real WakeOnDemandListener on a free port and pings it.</summary>
    private static async Task<PingResult> PingAgainstRealListener(
        Func<WakeStatus> status, PingLeg leg = PingLeg.Direct)
    {
        var free = FreePort();
        var listener = new WakeOnDemandListener();
        listener.Start(free, status, onJoinAttempt: () => { });
        try
        {
            return await new ServerPingService().PingAsync(leg, "127.0.0.1", free);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static WakeStatus Status() =>
        new("Un servidor", "1.21.4", 20, null, "Arrancando…");

    // --- The conversation ---

    [Fact]
    public async Task ARunningServerAnswersWithATime()
    {
        var result = await PingAgainstRealListener(Status);

        Assert.True(result.Answered, result.Error);
        Assert.InRange(result.Milliseconds!.Value, 0, 3000);
    }

    [Fact]
    public async Task TheLegAskedForIsTheLegReported()
    {
        // Both legs go through the same code; mixing them up would attribute the tunnel's delay to
        // the server and send somebody looking in the wrong place.
        var result = await PingAgainstRealListener(Status, PingLeg.Tunnel);

        Assert.Equal(PingLeg.Tunnel, result.Leg);
    }

    [Fact]
    public async Task NothingListeningIsNotAnError()
    {
        // A stopped server is the ordinary case, not a fault. It has to come back as "no answer"
        // with a reason, never as an exception that takes the panel down with it.
        var result = await new ServerPingService().PingAsync(PingLeg.Direct, "127.0.0.1", FreePort());

        Assert.False(result.Answered);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task AnAddressThatIsNotThereIsNotAnError()
    {
        var result = await new ServerPingService().PingAsync(
            PingLeg.Tunnel, "no-existe.invalid", 25565);

        Assert.False(result.Answered);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData("", 25565)]
    [InlineData("   ", 25565)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 70000)]
    public async Task NonsenseIsRefusedWithoutOpeningASocket(string host, int port)
    {
        var result = await new ServerPingService().PingAsync(PingLeg.Direct, host, port);

        Assert.False(result.Answered);
    }

    // --- Whose fault it is ---

    private static PingResult Ok(PingLeg leg, int ms) => new(leg, ms, null);
    private static PingResult Dead(PingLeg leg) => new(leg, null, "sin respuesta");

    [Fact]
    public void BothQuickIsFine()
    {
        Assert.Equal(PingVerdict.Fine,
            ServerPingService.Judge(Ok(PingLeg.Direct, 4), Ok(PingLeg.Tunnel, 38)));
    }

    [Fact]
    public void ASlowServerIsTheServersFaultEvenThoughTheTunnelIsSlowToo()
    {
        // The tunnel reading contains the server's, so when the server is struggling the tunnel
        // number is high as well. Reading them independently would blame playit for both.
        Assert.Equal(PingVerdict.ServerSlow,
            ServerPingService.Judge(Ok(PingLeg.Direct, 240), Ok(PingLeg.Tunnel, 275)));
    }

    [Fact]
    public void AQuickServerBehindASlowTunnelIsTheTunnelsFault()
    {
        Assert.Equal(PingVerdict.TunnelSlow,
            ServerPingService.Judge(Ok(PingLeg.Direct, 5), Ok(PingLeg.Tunnel, 315)));
    }

    [Fact]
    public void AServerThatAnswersHereAndNotThroughTheTunnelPointsAtTheTunnel()
    {
        // The case a plain "no data" would hide: from the outside the server looks dead, and from
        // here it plainly is not.
        Assert.Equal(PingVerdict.TunnelDown,
            ServerPingService.Judge(Ok(PingLeg.Direct, 4), Dead(PingLeg.Tunnel)));
    }

    [Fact]
    public void NeitherAnsweringBlamesNobody()
    {
        // Almost always just a stopped server. Naming a culprit here would be inventing one.
        Assert.Equal(PingVerdict.Unknown,
            ServerPingService.Judge(Dead(PingLeg.Direct), Dead(PingLeg.Tunnel)));
    }

    [Fact]
    public void ATunnelThatOnlyAddsItsUsualOverheadIsNotBlamed()
    {
        // Playit always adds something — it is a round trip to another machine. Flagging that as a
        // fault would leave the panel permanently accusing a tunnel that is working normally.
        Assert.Equal(PingVerdict.Fine,
            ServerPingService.Judge(Ok(PingLeg.Direct, 6), Ok(PingLeg.Tunnel, 90)));
    }

    // --- The pong has to be OUR pong ---

    /// <summary>
    /// A stream that swallows what is written and replays a prepared answer.
    /// </summary>
    /// <remarks>
    /// A real WakeOnDemandListener always echoes the payload correctly, so pointing the ping at one
    /// can never exercise the check that the echo matches — deleting that check broke no test at
    /// all. This is what lets a wrong answer be handed over deliberately.
    /// </remarks>
    private sealed class ScriptedStream(byte[] toRead) : Stream
    {
        private readonly MemoryStream _in = new(toRead);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _in.Length;
        public override long Position { get => _in.Position; set => _in.Position = value; }
        public override int Read(byte[] b, int o, int c) => _in.Read(b, o, c);
        public override void Write(byte[] b, int o, int c) { }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    /// <summary>Builds the two packets a server sends back: the status JSON, then a pong.</summary>
    private static byte[] Reply(byte[] pongPayload, int pongId = 0x01)
    {
        var buffer = new MemoryStream();
        WakeOnDemandListener.Send(buffer, 0x00, w => WakeOnDemandListener.WriteString(w, "{}"));
        WakeOnDemandListener.Send(buffer, pongId, w => w.Write(pongPayload, 0, pongPayload.Length));
        return buffer.ToArray();
    }

    [Fact]
    public void APongCarryingSomebodyElsesPayloadIsNotAnAnswer()
    {
        // The payload is echoed back unchanged by every real server. One that comes back different
        // is not the reply to our ping, and timing it would produce a number that means nothing.
        var result = ServerPingService.Measure(
            PingLeg.Direct, new ScriptedStream(Reply(new byte[8])), "127.0.0.1", 25565);

        Assert.False(result.Answered);
    }

    [Fact]
    public void APongThatIsTooShortIsNotAnAnswer()
    {
        var result = ServerPingService.Measure(
            PingLeg.Direct, new ScriptedStream(Reply(new byte[3])), "127.0.0.1", 25565);

        Assert.False(result.Answered);
    }

    [Fact]
    public void AReplyWithTheWrongPacketIdIsNotAnAnswer()
    {
        var result = ServerPingService.Measure(
            PingLeg.Direct, new ScriptedStream(Reply(new byte[8], pongId: 0x02)), "127.0.0.1", 25565);

        Assert.False(result.Answered);
    }

    [Fact]
    public void SilenceAfterTheHandshakeIsNotAnAnswer()
    {
        var result = ServerPingService.Measure(
            PingLeg.Direct, new ScriptedStream(Array.Empty<byte>()), "127.0.0.1", 25565);

        Assert.False(result.Answered);
    }

    // --- The guard that stops a socket being opened for nothing ---

    [Theory]
    [InlineData(null, 25565)]
    [InlineData("", 25565)]
    [InlineData("   ", 25565)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", -1)]
    [InlineData("127.0.0.1", 65536)]
    public void NonsenseIsNotAddressable(string? host, int port) =>
        Assert.False(ServerPingService.IsAddressable(host, port));

    [Theory]
    [InlineData("127.0.0.1", 25565)]
    [InlineData("findes.gl.joinmc.link", 65535)]
    [InlineData("localhost", 1)]
    public void ARealAddressIsAddressable(string host, int port) =>
        Assert.True(ServerPingService.IsAddressable(host, port));
}
