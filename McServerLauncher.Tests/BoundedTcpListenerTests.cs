using System.Net;
using System.Net.Sockets;
using System.Reflection;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The bounds the two wake-on-demand listeners share, checked on the shared class itself.
/// </summary>
public class BoundedTcpListenerTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    [Fact]
    public async Task ItHandsEachConnectionToTheCallback()
    {
        var port = FreePort();
        using var listener = new BoundedTcpListener(stream => stream.Write(new byte[] { 42 }));
        Assert.True(listener.Start(port));

        using var client = await ConnectAsync(port);
        var buffer = new byte[1];
        var read = await client.GetStream().ReadAsync(buffer);

        Assert.Equal(1, read);
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public void APortThatIsTakenIsNotAnErrorButAFalse()
    {
        var taken = new TcpListener(IPAddress.Any, 0);
        taken.Start();
        try
        {
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;
            using var listener = new BoundedTcpListener(_ => { });

            Assert.False(listener.Start(port));
            Assert.False(listener.IsListening);
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public async Task StopReleasesThePort()
    {
        var port = FreePort();
        var listener = new BoundedTcpListener(_ => { });
        Assert.True(listener.Start(port));

        listener.Stop();

        Assert.False(listener.IsListening);
        var again = new TcpListener(IPAddress.Any, port);
        again.Start();   // would throw while the first one still held it
        again.Stop();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ConnectionsPastTheCapAreClosedAtOnce()
    {
        var port = FreePort();
        using var release = new ManualResetEventSlim();
        using var listener = new BoundedTcpListener(_ => release.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(listener.Start(port));

        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < BoundedTcpListener.MaxConcurrentConnections; i++) held.Add(await ConnectAsync(port));
            await Task.Delay(300);   // let the accept loop hand every slot out

            using var extra = await ConnectAsync(port);
            var read = await extra.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, read);   // closed by the listener, not left waiting for a slot
        }
        finally
        {
            release.Set();
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public async Task ADeadlineClosesAConnectionThatKeepsTheCallbackBusy()
    {
        var port = FreePort();
        using var listener = new BoundedTcpListener(stream => stream.ReadByte())
        {
            ConnectionDeadline = TimeSpan.FromMilliseconds(500)
        };
        Assert.True(listener.Start(port));

        using var client = await ConnectAsync(port);
        // The client never sends a byte, so the callback is stuck in its read until the deadline closes it.
        var read = await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, read);
    }

    /// <summary>
    /// One definition of the bounds: neither wake listener may grow a socket loop of its own again.
    /// </summary>
    [Theory]
    [InlineData(typeof(WakeOnDemandListener))]
    [InlineData(typeof(NetherNetWakeListener))]
    public void TheWakeListenersOwnNoSocketOfTheirOwn(Type listener)
    {
        var fields = listener.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(TcpListener));
        Assert.Contains(fields, f => f.FieldType == typeof(BoundedTcpListener));
    }
}
