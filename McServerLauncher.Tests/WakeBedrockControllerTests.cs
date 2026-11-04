using System.Net;
using System.Net.Sockets;
using System.Text;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// How a sleeping server is wired to Bedrock: Geyser's TCP port is held while it sleeps when there is
/// crossplay, let go together with the Java one, and a join on it starts the server (issue 108).
/// </summary>
public class WakeBedrockControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wake-bedrock-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _said = new();
    private int _starts;

    public WakeBedrockControllerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* the OS cleans the temp folder */ }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private WakeOnDemandController Controller(int javaPort, bool crossplay, int bedrockPort) =>
        Controller(javaPort, crossplay, bedrockPort, out _);

    private WakeOnDemandController Controller(int javaPort, bool crossplay, int bedrockPort,
        out ServerConfig config)
    {
        File.WriteAllText(Path.Combine(_folder, "server.properties"), $"server-port={javaPort}\n");

        config = new ServerConfig
        {
            FolderPath = _folder,
            WakeOnDemand = true,
            CrossplayEnabled = crossplay,
            BedrockPort = bedrockPort
        };

        var host = new WakeHost(
            IsStarting: () => false,
            Motd: () => "Mi servidor",
            MaxPlayers: () => 8,
            CanStart: () => true,
            Say: line => _said.Add(line),
            Notify: (_, _) => { },
            StartServer: () => Interlocked.Increment(ref _starts),
            RunOnUi: action => action());   // no UI thread here: run it where it is called

        return new WakeOnDemandController(config, host, new ServerPropertiesService(),
            new WhitelistService(), new PlayersService());
    }

    private static bool IsFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private static string Post(int port)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;

        var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(
            "POST /v1/join/123 HTTP/1.1\r\nContent-Type: application/sdp\r\nContent-Length: 0\r\n\r\n");
        stream.Write(bytes, 0, bytes.Length);

        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void WithCrossplayAJoinOnGeysersPortStartsTheServer()
    {
        var java = FreePort();
        var bedrock = FreePort();
        using var controller = Controller(java, crossplay: true, bedrock);

        controller.Start();

        Assert.False(IsFree(java));
        Assert.False(IsFree(bedrock));

        Assert.StartsWith("HTTP/1.1 503", Post(bedrock));
        Assert.Equal(1, _starts);
        Assert.NotNull(controller.WokeAtUtc);   // the idle shutdown leaves a freshly woken server alone
    }

    [Fact]
    public void WithoutCrossplayGeysersPortIsLeftAlone()
    {
        var java = FreePort();
        var bedrock = FreePort();
        using var controller = Controller(java, crossplay: false, bedrock);

        controller.Start();

        Assert.False(IsFree(java));
        Assert.True(IsFree(bedrock));
    }

    [Fact]
    public void StoppingFreesBothPortsBeforeTheServerStarts()
    {
        // Geyser opens its signaling on this port a few seconds into the start; with the app still
        // holding it, Bedrock would be refused for as long as the server is up.
        var java = FreePort();
        var bedrock = FreePort();
        using var controller = Controller(java, crossplay: true, bedrock);
        controller.Start();

        controller.Stop();

        Assert.True(IsFree(java));
        Assert.True(IsFree(bedrock));
    }

    [Fact]
    public void ABedrockPortSameAsJavasIsNotBoundTwice()
    {
        // The Java listener already holds it; binding again would fail and put a "port taken" line
        // in the console about a port the app itself is holding.
        var port = FreePort();
        using var controller = Controller(port, crossplay: true, port);

        controller.Start();

        Assert.DoesNotContain(_said, line => line.Length > 0);
    }

    [Fact]
    public void ABusyBedrockPortIsSaidAndJavaKeepsWorking()
    {
        var java = FreePort();
        var bedrock = FreePort();
        var geyser = new TcpListener(IPAddress.Any, bedrock);   // a Geyser left running
        geyser.Start();
        try
        {
            using var controller = Controller(java, crossplay: true, bedrock);

            controller.Start();

            Assert.False(IsFree(java));
            Assert.Single(_said);
            Assert.Contains(bedrock.ToString(), _said[0]);
        }
        finally { geyser.Stop(); }
    }

    [Fact]
    public void TurningWakeOnDemandOffLetsBothGo()
    {
        var java = FreePort();
        var bedrock = FreePort();
        using var controller = Controller(java, crossplay: true, bedrock, out var config);
        controller.Start();

        // Same controller, config switched off: Start is what the effects table calls.
        config.WakeOnDemand = false;
        controller.Start();

        Assert.True(IsFree(java));
        Assert.True(IsFree(bedrock));
    }
}
