using System.Net;
using System.Net.Sockets;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Finding a free UDP port for Bedrock.
/// </summary>
/// <remarks>
/// TCP and UDP are separate namespaces, so a port can be busy on one and free on the other. Asking
/// the TCP table about a UDP port is not a near miss — it hands out a port something else already
/// holds, and the only symptom is a Geyser that quietly fails to bind. These use real sockets
/// because the whole question is what the operating system reports.
/// </remarks>
public class UdpPortTests
{
    [Fact]
    public void AnOpenUdpPortIsSeenAsBusy()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        Assert.True(new PortService().IsUdpPortInUse(port));
    }

    [Fact]
    public void ClosingItFreesItAgain()
    {
        int port;
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        // A port left permanently "in use" after its holder went away would push every new Bedrock
        // server one number further along for ever.
        Assert.False(new PortService().IsUdpPortInUse(port));
    }

    [Fact]
    public void TheUdpCheckIsNotJustTheTcpOneRenamed()
    {
        // The test that proves the two tables are actually different: a TCP listener must not make
        // the same number look busy to the UDP check. Get this wrong and Bedrock ports are chosen
        // by consulting a table that says nothing about them.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ports = new PortService();

            Assert.True(ports.IsPortInUse(port));
            Assert.False(ports.IsUdpPortInUse(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void TheSearchSkipsAPortThatIsAlreadyOpen()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var taken = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        var found = new PortService().FindFreeUdpPort(taken, new HashSet<int>());

        Assert.NotNull(found);
        Assert.NotEqual(taken, found);
        Assert.True(found > taken);
    }

    [Fact]
    public void TheSearchAlsoSkipsPortsWeAlreadyHandedOut()
    {
        // Two Bedrock servers configured back to back: the second must not be given the first's
        // port just because nothing has bound it yet.
        var avoid = new HashSet<int> { 19132, 19133 };

        var found = new PortService().FindFreeUdpPort(19132, avoid);

        Assert.NotNull(found);
        Assert.DoesNotContain(found!.Value, avoid);
    }
}
