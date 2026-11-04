using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The listener that lets a Bedrock player wake a sleeping server (issue 108). Behind a Playit tunnel
/// every byte of this arrives from the open internet, so the hostile inputs matter as much as the
/// well-formed ones, and the one rule that matters most is when it wakes the server: on a join, and
/// not on the capability check the client may send just to draw the server.
/// </summary>
public class NetherNetWakeListenerTests
{
    private static WakeStatus Status() => new(
        Description: "Mi servidor\n§r§e§lApagado",
        VersionName: "1.26.60",
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

    /// <summary>One request, and everything the listener sends back until it closes.</summary>
    private static string Exchange(int port, string raw) => Exchange(port, Encoding.ASCII.GetBytes(raw));

    private static string Exchange(int port, byte[] bytes)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;

        try
        {
            var stream = client.GetStream();
            stream.Write(bytes, 0, bytes.Length);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;   // hung up on, which is the expected answer to garbage
        }
    }

    private static string Body(string response) => response[(response.IndexOf("\r\n\r\n") + 4)..];

    private static string Join(string id = "9876543210", string body = "v=0\r\n") =>
        $"POST /v1/join/{id} HTTP/1.1\r\nHost: x\r\nContent-Type: application/sdp\r\n"
        + $"Content-Length: {body.Length}\r\n\r\n{body}";

    private const string Check = "GET /v1/join HTTP/1.1\r\nHost: x\r\n\r\n";

    [Fact]
    public void TheCapabilityCheckGetsTheServersStatus()
    {
        var port = FreePort();
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => { }));

        var response = Exchange(port, Check);

        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("application/json", response);

        using var doc = JsonDocument.Parse(Body(response));
        var root = doc.RootElement;
        Assert.Equal("1.26.60", root.GetProperty("version").GetString());
        Assert.Equal(8, root.GetProperty("maxPlayers").GetInt32());
        Assert.Equal(0, root.GetProperty("players").GetInt32());
    }

    [Fact]
    public void TheNameLeavesOutTheNoticeAndTheColourCodes()
    {
        // The second line is the "asleep" notice for the Java list, and the colour codes are
        // Java's: Bedrock would draw them as literal characters.
        using var doc = JsonDocument.Parse(NetherNetWakeListener.BuildStatusJson(Status()));

        Assert.Equal("Mi servidor", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("Mi servidor", doc.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void TheCapabilityCheckAloneDoesNotWakeTheServer()
    {
        // The client may send it only to draw the server. Waking on it would start the server, and
        // a backup in front of it, for people who are not even playing.
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        for (var i = 0; i < 3; i++) Exchange(port, Check);

        Assert.Equal(0, wakes);
    }

    [Fact]
    public void AJoinWakesTheServerOnceAndIsRefused()
    {
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        var response = Exchange(port, Join());

        Assert.Equal(1, wakes);
        // Not a 2xx: that is a signaling failure to the client, which then does not try WebRTC
        // against a server that is not there yet.
        Assert.StartsWith("HTTP/1.1 503", response);
    }

    [Theory]
    [InlineData("POST /v1/join HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]            // no id
    [InlineData("POST /v1/join/ HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]           // empty id
    [InlineData("GET /v1/join/123 HTTP/1.1\r\n\r\n")]                              // wrong method
    [InlineData("POST /v1/other HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]           // wrong path
    [InlineData("POST /v1/join/%00%2e%2e HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]  // not an id
    [InlineData("DELETE /v1/join/123 HTTP/1.1\r\n\r\n")]
    public void OnlyARealJoinWakesTheServer(string raw)
    {
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        var response = Exchange(port, raw);

        Assert.Equal(0, wakes);
        Assert.StartsWith("HTTP/1.1 404", response);
    }

    [Fact]
    public void AnIdOfAnAbsurdLengthDoesNotWake()
    {
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        Exchange(port, Join(id: new string('7', 500)));

        Assert.Equal(0, wakes);
    }

    [Fact]
    public void GarbageGetsNoAnswerAndWakesNothing()
    {
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        // A Java handshake, a TLS hello, and a request that never ends its headers: none of them
        // is this protocol, and none may cost more than a closed connection.
        var javaHandshake = new byte[] { 0x10, 0x00, 0xFF, 0x05, 0x6C, 0x6F, 0x63, 0x61, 0x6C, 0x63, 0xDD, 0x01 };
        var tlsHello = new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00, 0x01, 0xFC };
        Assert.Equal(string.Empty, Exchange(port, javaHandshake));
        Assert.Equal(string.Empty, Exchange(port, tlsHello));
        Assert.Equal(string.Empty, Exchange(port, "GET /v1/join HTTP/1.1\r\n" + new string('A', 20_000)));

        Assert.Equal(0, wakes);
    }

    [Fact]
    public void ABadContentLengthIsRefused()
    {
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        Assert.Equal(string.Empty, Exchange(port,
            "POST /v1/join/123 HTTP/1.1\r\nContent-Length: banana\r\n\r\n"));
        Assert.Equal(string.Empty, Exchange(port,
            "POST /v1/join/123 HTTP/1.1\r\nContent-Length: -5\r\n\r\n"));

        Assert.Equal(0, wakes);
    }

    [Fact]
    public void AHugeDeclaredBodyIsNotReadButTheJoinStillCounts()
    {
        // The wake does not depend on the body, so a length no real offer reaches costs nothing:
        // the body is not read, and nothing is allocated for it.
        var port = FreePort();
        var wakes = 0;
        using var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => Interlocked.Increment(ref wakes)));

        var response = Exchange(port,
            "POST /v1/join/123 HTTP/1.1\r\nContent-Length: 4000000000\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 503", response);
        Assert.Equal(1, wakes);
    }

    // --- request parsing, without a socket -----------------------------------------------------

    [Fact]
    public void TheRequestLineAndLengthAreRead()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "POST /v1/join/42?x=1 HTTP/1.1\r\nhost: a\r\ncontent-length: 12\r\n\r\nBODY"));

        var request = NetherNetWakeListener.ReadRequest(stream);

        Assert.NotNull(request);
        Assert.Equal("POST", request!.Value.Method);
        Assert.Equal("/v1/join/42", request.Value.Path);   // the query is not part of the path
        Assert.Equal(12, request.Value.ContentLength);
        Assert.Equal('B', (char)stream.ReadByte());        // the body is left for the caller
    }

    [Theory]
    [InlineData("")]
    [InlineData("GET /v1/join HTTP/1.1\r\n")]               // never finishes the headers
    [InlineData("GET /v1/join\r\n\r\n")]                    // no version
    [InlineData("GET v1/join HTTP/1.1\r\n\r\n")]            // not a path
    [InlineData("GET /v1/join SPDY/3\r\n\r\n")]             // not HTTP
    [InlineData("GET /v1/join HTTP/1.1 extra\r\n\r\n")]
    public void ARequestThatIsNotHttpIsRefused(string raw)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));

        Assert.Null(NetherNetWakeListener.ReadRequest(stream));
    }

    [Theory]
    [InlineData("/v1/join/1", true)]
    [InlineData("/v1/join/9876543210123", true)]
    [InlineData("/v1/join/a-b_c.d", true)]
    [InlineData("/v1/join/", false)]
    [InlineData("/v1/join", false)]
    [InlineData("/v1/join/a/b", false)]
    [InlineData("/v1/join/a b", false)]
    public void WhatCountsAsAJoinPath(string path, bool expected) =>
        Assert.Equal(expected, NetherNetWakeListener.IsJoinPath(path));

    // --- the same bounds as the Java listener --------------------------------------------------

    [Fact]
    public async Task ASlowDripConnectionIsCutOffByTheDeadline()
    {
        var port = FreePort();
        using var listener = new NetherNetWakeListener { ConnectionDeadline = TimeSpan.FromSeconds(2) };
        Assert.True(listener.Start(port, Status, () => { }));

        using var slow = new TcpClient();
        slow.Connect(IPAddress.Loopback, port);
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = slow.GetStream();
                var bytes = Encoding.ASCII.GetBytes("GET /v1/join HTTP/1.1\r\n");
                stream.Write(bytes, 0, bytes.Length);

                // One header byte at a time, each one resetting the per-read timeout.
                for (var i = 0; i < 200; i++)
                {
                    stream.WriteByte((byte)'a');
                    stream.Flush();
                    await Task.Delay(400);
                }
            }
            catch { /* being hung up on is the expected outcome */ }
        });

        var cutOff = await WaitUntil(() => !IsStillConnected(slow), TimeSpan.FromSeconds(8));

        Assert.True(cutOff, "la conexion lenta seguia viva pasado el plazo");
    }

    [Fact]
    public void StoppingFreesThePortForGeyser()
    {
        // The point of the whole design: once the server is starting, Geyser has to be able to open
        // its own signaling on this very port.
        var port = FreePort();
        var listener = new NetherNetWakeListener();
        Assert.True(listener.Start(port, Status, () => { }));
        Assert.True(listener.IsListening);

        listener.Stop();

        Assert.False(listener.IsListening);
        var geyser = new TcpListener(IPAddress.Any, port);
        geyser.Start();   // throws if the port is still held
        geyser.Stop();
    }

    [Fact]
    public void ABusyPortIsReportedNotThrown()
    {
        var port = FreePort();
        var taken = new TcpListener(IPAddress.Any, port);
        taken.Start();
        try
        {
            using var listener = new NetherNetWakeListener();

            Assert.False(listener.Start(port, Status, () => { }));
            Assert.False(listener.IsListening);
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public void StartingAgainMovesTheListener()
    {
        var first = FreePort();
        var second = FreePort();
        using var listener = new NetherNetWakeListener();

        Assert.True(listener.Start(first, Status, () => { }));
        Assert.True(listener.Start(second, Status, () => { }));

        Assert.StartsWith("HTTP/1.1 200", Exchange(second, Check));
        var old = new TcpListener(IPAddress.Any, first);
        old.Start();   // the first port was let go
        old.Stop();
    }

    private static bool IsStillConnected(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            return !(socket.Poll(1000, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch { return false; }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
