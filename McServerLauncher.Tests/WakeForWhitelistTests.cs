using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A sleeping server with a whitelist wakes only for somebody on it.
/// </summary>
/// <remarks>
/// Pressing Join used to wake it for anyone, and behind a Playit tunnel "anyone" includes every
/// scanner that looks for Minecraft servers and tries to log in — each of them started the server
/// and the backup in front of the start.
/// </remarks>
public class WakeForWhitelistTests
{
    private static readonly string[] Listed = { "Alice" };
    private static readonly string[] Ops = { "Owner" };
    private static readonly string[] None = Array.Empty<string>();

    // --- The rule ---

    [Theory]
    [InlineData("Alice", true)]
    [InlineData("alice", true)]      // names are not case-sensitive in Minecraft
    [InlineData("Owner", true)]      // operators get in past a whitelist, so they may wake it
    [InlineData("Scanner", false)]
    [InlineData(null, false)]        // a client that sent no name is on no list
    [InlineData("bad name!", false)]
    public void WithTheWhitelistOnOnlyItsPlayersWakeIt(string? player, bool wakes) =>
        Assert.Equal(wakes, WakePolicy.Allows(player, whitelistOn: true, onlyWhitelisted: true, Listed, Ops));

    [Theory]
    [InlineData("Scanner")]
    [InlineData(null)]
    public void WithTheWhitelistOffAnyoneWakesIt(string? player) =>
        Assert.True(WakePolicy.Allows(player, whitelistOn: false, onlyWhitelisted: true, None, None));

    [Fact]
    public void TheOwnerCanLetAnyoneWakeItAnyway() =>
        Assert.True(WakePolicy.Allows("Scanner", whitelistOn: true, onlyWhitelisted: false, Listed, Ops));

    // --- The name, from the client's Login Start ---

    [Fact]
    public void TheNameIsReadFromLoginStart()
    {
        using var stream = new MemoryStream(LoginStart("Alice"));
        Assert.Equal("Alice", WakeOnDemandListener.ReadLoginName(stream));
    }

    [Fact]
    public void NoLoginStartMeansNoName()
    {
        using var stream = new MemoryStream();
        Assert.Null(WakeOnDemandListener.ReadLoginName(stream));
    }

    // --- End to end, over a real socket ---

    [Fact]
    public async Task AJoinFromSomebodyNotListedIsToldSoAndDoesNotWakeIt()
    {
        var (asked, message) = await JoinAs("Scanner", allow: false);

        Assert.Equal("Scanner", asked);
        Assert.Equal("refused", message);
    }

    [Fact]
    public async Task AJoinFromSomebodyListedWakesIt()
    {
        var (asked, message) = await JoinAs("Alice", allow: true);

        Assert.Equal("Alice", asked);
        Assert.Equal("waking", message);
    }

    /// <summary>Connects as <paramref name="player"/>; returns who the listener asked about, and what it said.</summary>
    private static async Task<(string? Asked, string? Message)> JoinAs(string player, bool allow)
    {
        var port = FreePort();
        string? asked = null;

        using var listener = new WakeOnDemandListener();
        Assert.True(listener.Start(port,
            () => new WakeStatus("motd", "1.21.1", 10, null, "waking", "refused"),
            name => { asked = name; return allow; }));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.ReceiveTimeout = client.SendTimeout = 3000;
        var stream = client.GetStream();

        using (var handshake = new MemoryStream())
        {
            WakeOnDemandListener.WriteVarInt(handshake, 0x00);
            WakeOnDemandListener.WriteVarInt(handshake, 767);
            WakeOnDemandListener.WriteString(handshake, "localhost");
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)(port & 0xFF));
            WakeOnDemandListener.WriteVarInt(handshake, 2);   // next state: login
            Frame(stream, handshake.ToArray());
        }
        stream.Write(LoginStart(player));
        stream.Flush();

        // The disconnect: length, packet id 0x00, then a JSON text component.
        WakeOnDemandListener.TryReadVarInt(stream);
        WakeOnDemandListener.TryReadVarInt(stream);
        var json = WakeOnDemandListener.ReadString(stream);
        using var doc = JsonDocument.Parse(json);
        return (asked, doc.RootElement.GetProperty("text").GetString());
    }

    /// <summary>A framed Login Start: the name, then a UUID as modern clients send it.</summary>
    private static byte[] LoginStart(string name)
    {
        using var body = new MemoryStream();
        WakeOnDemandListener.WriteVarInt(body, 0x00);
        WakeOnDemandListener.WriteString(body, name);
        body.Write(new byte[16]);

        using var framed = new MemoryStream();
        Frame(framed, body.ToArray());
        return framed.ToArray();
    }

    private static void Frame(Stream stream, byte[] payload)
    {
        WakeOnDemandListener.WriteVarInt(stream, payload.Length);
        stream.Write(payload, 0, payload.Length);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
