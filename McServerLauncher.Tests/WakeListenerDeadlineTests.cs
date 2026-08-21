using System.Net;
using System.Net.Sockets;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The connection deadline. Behind a Playit tunnel this socket is on the open internet, and the
/// failure it guards against is not a crash but something quieter: every slot taken, so the server
/// can no longer be woken and nothing anywhere says why.
/// </summary>
public class WakeListenerDeadlineTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromSeconds(2);

    private static WakeStatus Status() => new(
        Description: "prueba",
        VersionName: "1.21.1",
        MaxPlayers: 8,
        IconPath: null,
        DisconnectMessage: "encendiendo");

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Announces a big packet and then trickles, the way ReceiveTimeout alone can't catch.</summary>
    private static TcpClient StartSlowClient(int port)
    {
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);

        _ = Task.Run(async () =>
        {
            try
            {
                var stream = client.GetStream();
                stream.WriteByte(0xE8);     // a length VarInt announcing ~1000 bytes...
                stream.WriteByte(0x07);
                stream.Flush();

                // ...delivered one byte at a time, each one resetting ReceiveTimeout.
                for (var i = 0; i < 200; i++)
                {
                    stream.WriteByte(0x00);
                    stream.Flush();
                    await Task.Delay(400);
                }
            }
            catch { /* being hung up on is the expected outcome */ }
        });

        return client;
    }

    [Fact]
    public async Task SlowDripConnectionIsCutOffByTheDeadline()
    {
        var port = FreePort();
        using var listener = new WakeOnDemandListener { ConnectionDeadline = ShortDeadline };
        Assert.True(listener.Start(port, Status, () => { }));

        using var slow = StartSlowClient(port);

        // Well inside what a per-read timeout would allow: the client is sending steadily, so
        // ReceiveTimeout would never fire. Only a whole-connection deadline ends this.
        var cutOff = await WaitUntil(() => !IsStillConnected(slow), TimeSpan.FromSeconds(8));

        Assert.True(cutOff, "la conexión lenta seguía viva pasado el plazo");
    }

    [Fact]
    public async Task SlowClientsCannotLockOutARealOne()
    {
        // The point of the whole finding: filling every slot must not be able to stop the server
        // from being woken. Before the deadline existed, this stayed broken until the app restarted.
        var port = FreePort();
        using var listener = new WakeOnDemandListener { ConnectionDeadline = ShortDeadline };
        Assert.True(listener.Start(port, Status, () => { }));

        var hogs = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 20; i++) hogs.Add(StartSlowClient(port));

            // Once the deadline has swept them, a normal handshake must be answered again.
            var served = await WaitUntil(() => CanCompleteStatusHandshake(port), TimeSpan.FromSeconds(12));
            Assert.True(served, "el servidor no podía despertarse con las conexiones lentas abiertas");
        }
        finally
        {
            foreach (var c in hogs) { try { c.Dispose(); } catch { /* going away */ } }
        }
    }

    [Fact]
    public async Task NormalClientIsStillServedPromptly()
    {
        // The deadline must not have made the ordinary path any less willing.
        var port = FreePort();
        using var listener = new WakeOnDemandListener { ConnectionDeadline = ShortDeadline };
        Assert.True(listener.Start(port, Status, () => { }));

        Assert.True(await WaitUntil(() => CanCompleteStatusHandshake(port), TimeSpan.FromSeconds(5)));
    }

    // --- plumbing ---

    private static bool IsStillConnected(TcpClient client)
    {
        try
        {
            var socket = client.Client;

            // Readable with nothing to read means the peer closed: the standard way to tell a live
            // idle socket from a dead one without consuming anything.
            if (socket.Poll(0, SelectMode.SelectRead)) return socket.Available > 0;
            return true;
        }
        catch { return false; }
    }

    private static bool CanCompleteStatusHandshake(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = client.SendTimeout = 2000;
            var stream = client.GetStream();

            using var payload = new MemoryStream();
            WakeOnDemandListener.WriteVarInt(payload, 0x00);
            WakeOnDemandListener.WriteVarInt(payload, 767);
            WakeOnDemandListener.WriteString(payload, "127.0.0.1");
            payload.WriteByte((byte)(port >> 8));
            payload.WriteByte((byte)(port & 0xFF));
            WakeOnDemandListener.WriteVarInt(payload, 1);       // next state: status

            var bytes = payload.ToArray();
            WakeOnDemandListener.WriteVarInt(stream, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);

            WakeOnDemandListener.WriteVarInt(stream, 1);        // status request: one byte of body
            stream.WriteByte(0x00);
            stream.Flush();

            var length = WakeOnDemandListener.TryReadVarInt(stream);
            return length is > 0;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(150);
        }
        return condition();
    }
}
