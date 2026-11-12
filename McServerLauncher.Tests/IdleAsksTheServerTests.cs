using System.Net;
using System.Net.Sockets;
using System.Text;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The second opinion behind the idle shutdown: how many players the server itself says it has,
/// asked with the status ping every client's server list sends. It exists because the console has
/// been wrong more than once (issue #167), and a wrong "nobody is on" stops a server with people
/// playing.
/// </summary>
public class IdleAsksTheServerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "idle-ask-" + Guid.NewGuid().ToString("N"));

    public IdleAsksTheServerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* the OS cleans the temp folder */ }
    }

    // --- reading the reply ---

    [Theory]
    [InlineData("""{"version":{"name":"26.3","protocol":776},"players":{"max":20,"online":3}}""", 3)]
    [InlineData("""{"players":{"max":20,"online":0,"sample":[]},"description":"x"}""", 0)]
    public void ACountInTheReplyIsKnown(string json, int online)
    {
        var status = ConnectionProbe.ReadStatus(json);

        Assert.True(status.PlayersKnown);
        Assert.Equal(online, status.Online);
    }

    /// <summary>
    /// Zero is what an absent count used to read as. For the idle shutdown that is the dangerous
    /// direction: "the server did not say" must never become "the server says nobody is on".
    /// </summary>
    [Theory]
    [InlineData("""{"version":{"name":"26.3","protocol":776},"description":"x"}""")]
    [InlineData("""{"players":{"max":20}}""")]
    [InlineData("""{"players":{"online":"three"}}""")]
    [InlineData("""{"players":7}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void AReplyWithoutACountIsNotZeroPlayers(string json) =>
        Assert.False(ConnectionProbe.ReadStatus(json).PlayersKnown);

    [Theory]
    [InlineData(null, "127.0.0.1")]
    [InlineData("", "127.0.0.1")]
    [InlineData("  ", "127.0.0.1")]
    [InlineData("0.0.0.0", "127.0.0.1")]
    [InlineData("::", "127.0.0.1")]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData(" 10.0.0.2 ", "10.0.0.2")]
    public void AServerBoundToOneAddressIsAskedThere(string? serverIp, string expected) =>
        Assert.Equal(expected, ConnectionProbe.LocalAddressFor(serverIp));

    // --- asking a server ---

    /// <summary>A server that answers one status ping with <paramref name="json"/>, like the real one.</summary>
    private static (int Port, Task Served) FakeServer(string? json)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var served = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;

                ReadPacket(stream);   // handshake
                ReadPacket(stream);   // status request
                if (json is null) return;   // enable-status=false: the server just hangs up

                var body = new MemoryStream();
                ConnectionProbe.WriteVarInt(body, 0x00);
                var text = Encoding.UTF8.GetBytes(json);
                ConnectionProbe.WriteVarInt(body, text.Length);
                body.Write(text);
                WritePacket(stream, body.ToArray());

                WritePacket(stream, ReadPacket(stream));   // the ping, echoed
            }
            catch { /* the client went away: the test's own assertion says what mattered */ }
            finally { listener.Stop(); }
        });

        return (port, served);
    }

    private static byte[] ReadPacket(Stream stream)
    {
        var buffer = new byte[ConnectionProbe.ReadVarInt(stream)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WritePacket(Stream stream, byte[] payload)
    {
        var framed = new MemoryStream();
        ConnectionProbe.WriteVarInt(framed, payload.Length);
        framed.Write(payload);
        stream.Write(framed.ToArray());
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AServerWithPeopleOnSaysHowMany()
    {
        var (port, served) = FakeServer("""{"version":{"name":"26.3","protocol":776},"players":{"max":20,"online":4}}""");

        Assert.Equal(4, await ConnectionProbe.PlayersOnlineAsync(null, port, Wait));
        await served;
    }

    [Fact]
    public async Task AnEmptyServerSaysZero()
    {
        var (port, served) = FakeServer("""{"players":{"max":20,"online":0}}""");

        Assert.Equal(0, await ConnectionProbe.PlayersOnlineAsync("", port, Wait));
        await served;
    }

    [Fact]
    public async Task AServerThatHangsUpGivesNoAnswer()
    {
        var (port, served) = FakeServer(json: null);

        Assert.Null(await ConnectionProbe.PlayersOnlineAsync(null, port, Wait));
        await served;
    }

    [Fact]
    public async Task AServerThatAnswersWithoutACountGivesNoAnswer()
    {
        var (port, served) = FakeServer("""{"description":"hidden"}""");

        Assert.Null(await ConnectionProbe.PlayersOnlineAsync(null, port, Wait));
        await served;
    }

    [Fact]
    public async Task NothingListeningGivesNoAnswer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Assert.Null(await ConnectionProbe.PlayersOnlineAsync(null, port, TimeSpan.FromSeconds(2)));
    }

    // --- the time a woken server waits for its player ---

    private WakeOnDemandController Controller(Action start)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        File.WriteAllText(Path.Combine(_folder, "server.properties"), $"server-port={port}\n");

        var config = new ServerConfig { FolderPath = _folder, WakeOnDemand = true };
        var host = new WakeHost(
            IsStarting: () => false,
            Motd: () => "x",
            MaxPlayers: () => 8,
            CanStart: () => true,
            Say: _ => { },
            Notify: (_, _) => { },
            StartServer: start,
            RunOnUi: action => action());

        return new WakeOnDemandController(config, host, new ServerPropertiesService(),
            new WhitelistService(), new PlayersService());
    }

    private void Knock()
    {
        var port = new ServerPropertiesService().GetServerPort(Path.Combine(_folder, "server.properties"))!.Value;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;
        var stream = client.GetStream();

        var handshake = new MemoryStream();
        ConnectionProbe.WriteVarInt(handshake, 0x00);
        ConnectionProbe.WriteVarInt(handshake, 776);
        ConnectionProbe.WriteVarInt(handshake, 9);
        handshake.Write(Encoding.UTF8.GetBytes("localhost"));
        handshake.Write([0x63, 0xDD]);
        ConnectionProbe.WriteVarInt(handshake, 2);   // login
        WritePacket(stream, handshake.ToArray());

        var login = new MemoryStream();
        ConnectionProbe.WriteVarInt(login, 0x00);
        ConnectionProbe.WriteVarInt(login, 5);
        login.Write(Encoding.UTF8.GetBytes("Alice"));
        WritePacket(stream, login.ToArray());

        ReadPacket(stream);   // the disconnect: by now the wake has been asked for
    }

    /// <summary>
    /// A modded server can take longer to start than the whole grace. Counted from the knock, it
    /// was ready, empty and out of grace at once, and stopped before its player saw it come up.
    /// </summary>
    [Fact]
    public void TheGraceOfAWokenServerRunsFromWhenItIsReady()
    {
        var starts = 0;
        using var controller = Controller(() => starts++);
        controller.Start();

        Knock();
        Assert.Equal(1, starts);
        var knockedAt = controller.WokeAtUtc;
        Assert.NotNull(knockedAt);

        Thread.Sleep(60);          // the server starting
        controller.Stop();
        controller.ServerReady();

        Assert.True(controller.WokeAtUtc > knockedAt);
        Assert.True(IdlePolicy.IsWithinWakeGrace(controller.WokeAtUtc, DateTime.UtcNow));
    }

    [Fact]
    public void AServerNobodyWokeGetsNoGrace()
    {
        using var controller = Controller(() => { });

        controller.ServerReady();   // started by hand

        Assert.Null(controller.WokeAtUtc);
    }

    /// <summary>Only the start the knock caused: a later one, by hand, does not move the clock.</summary>
    [Fact]
    public void ALaterStartByHandDoesNotRenewTheGrace()
    {
        using var controller = Controller(() => { });
        controller.Start();
        Knock();
        controller.Stop();
        controller.ServerReady();
        var first = controller.WokeAtUtc;

        Thread.Sleep(30);
        controller.ServerReady();

        Assert.Equal(first, controller.WokeAtUtc);
    }
}
