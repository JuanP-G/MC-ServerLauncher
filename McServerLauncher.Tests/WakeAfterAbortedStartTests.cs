using System.Net;
using System.Net.Sockets;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A server woken on demand whose start is abandoned goes back to listening.
/// </summary>
/// <remarks>
/// Waking stops the listener first, so the real server can take the port. Only the transition to
/// Stopped used to start it again — and a start abandoned before the process exists never makes that
/// transition. The server then stayed asleep and deaf until the app was restarted, with nothing to
/// say so. Here the start is abandoned the way a real one is: a Paper server in a folder with a "+"
/// in its name, which an unattended start refuses rather than ask about.
/// </remarks>
[Collection("avalonia")]
public class WakeAfterAbortedStartTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-wake+" + Guid.NewGuid().ToString("N"));

    public WakeAfterAbortedStartTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public async Task AnAbandonedWakeLeavesTheServerListening()
    {
        var port = FreePort();
        File.WriteAllText(Path.Combine(_folder, "server.properties"), $"server-port={port}\n");
        var config = new ServerConfig { Name = "survival", FolderPath = _folder, Type = ServerType.Paper };

        ServerViewModel? server = null;
        _ui.Run(() =>
        {
            server = new ServerViewModel(config);
            config.WakeOnDemand = true;          // opens the listener, as ticking the box does
        });

        await Knock(port);

        // The start ran and gave up on the path: that is the line it leaves behind.
        var refused = string.Format(Localizer.Get("Msg_BukkitPathFmt"), '+');
        Assert.True(await WaitUntil(() => Lines(server!).Contains(refused)), "the start never ran");

        Assert.True(await WaitUntil(() => Accepts(port)), "nothing is listening after the start gave up");

        _ui.Run(() => server!.ShutdownAsync().GetAwaiter().GetResult());
    }

    private List<string> Lines(ServerViewModel server)
    {
        var lines = new List<string>();
        _ui.Run(() => lines.AddRange(server.ConsoleLines.Select(l => l.Text)));
        return lines;
    }

    /// <summary>Presses Join: a handshake for login, then Login Start.</summary>
    private static async Task Knock(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.ReceiveTimeout = 3000;
        var stream = client.GetStream();

        using var handshake = new MemoryStream();
        WakeOnDemandListener.WriteVarInt(handshake, 0x00);
        WakeOnDemandListener.WriteVarInt(handshake, 767);
        WakeOnDemandListener.WriteString(handshake, "localhost");
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)(port & 0xFF));
        WakeOnDemandListener.WriteVarInt(handshake, 2);
        Frame(stream, handshake.ToArray());

        using var login = new MemoryStream();
        WakeOnDemandListener.WriteVarInt(login, 0x00);
        WakeOnDemandListener.WriteString(login, "Alice");
        Frame(stream, login.ToArray());

        WakeOnDemandListener.TryReadVarInt(stream);   // the disconnect: it answered, so it woke
    }

    private static void Frame(Stream stream, byte[] payload)
    {
        WakeOnDemandListener.WriteVarInt(stream, payload.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static bool Accepts(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
