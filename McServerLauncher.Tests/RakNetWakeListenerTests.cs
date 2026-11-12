using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The sleeping server on Bedrock's UDP port: a client that speaks RakNet sees it in its list and
/// wakes it by pressing Join. Behind a Playit tunnel every datagram arrives from the open internet
/// with an address that can be forged, so what is refused matters as much as what is answered.
/// </summary>
public class RakNetWakeListenerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wake-raknet-" + Guid.NewGuid().ToString("N"));

    public RakNetWakeListenerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* the OS cleans the temp folder */ }
    }

    private static readonly WakeStatus Status = new(
        Description: "§aMi servidor; el bueno\n§7Apagado · entra para encenderlo",
        VersionName: "26.3",
        MaxPlayers: 8,
        IconPath: null,
        DisconnectMessage: "x");

    private static byte[] Ping(long time = 0x0102030405060708, byte id = RakNetWakeListener.UnconnectedPing)
    {
        var packet = new byte[33];
        packet[0] = id;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1), time);
        RakNetWakeListener.Magic.CopyTo(packet, 9);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(25), 42);
        return packet;
    }

    /// <summary>What a client sends on Join: the magic, RakNet's version, and zeros up to the size it probes.</summary>
    private static byte[] JoinRequest(int size = 1464)
    {
        var packet = new byte[size];
        packet[0] = RakNetWakeListener.OpenConnectionRequest1;
        RakNetWakeListener.Magic.CopyTo(packet, 1);
        packet[17] = 11;
        return packet;
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static string Advertisement(byte[] pong)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(pong.AsSpan(33));
        return Encoding.UTF8.GetString(pong, 35, length);
    }

    // --- the pong ---

    [Fact]
    public void APongEchoesTheClientsClockAndCarriesTheMagic()
    {
        var pong = RakNetWakeListener.BuildPong(Ping().AsSpan(1, 8), guid: 7, Status, port: 19132);

        Assert.Equal(RakNetWakeListener.UnconnectedPong, pong[0]);
        Assert.Equal(0x0102030405060708, BinaryPrimitives.ReadInt64BigEndian(pong.AsSpan(1)));
        Assert.Equal(7, BinaryPrimitives.ReadInt64BigEndian(pong.AsSpan(9)));
        Assert.True(pong.AsSpan(17, 16).SequenceEqual(RakNetWakeListener.Magic));
        Assert.Equal(pong.Length - 35, BinaryPrimitives.ReadUInt16BigEndian(pong.AsSpan(33)));
    }

    [Fact]
    public void TheListEntrySaysTheServerIsAsleepOnTheLineBedrockShows()
    {
        var fields = RakNetWakeListener.BuildAdvertisement(Status, guid: 7, port: 19132).Split(';');

        Assert.Equal("MCPE", fields[0]);
        Assert.Equal("Mi servidor, el bueno - Apagado · entra para encenderlo", fields[1]);
        Assert.Equal("26.3", fields[3]);
        Assert.Equal("0", fields[4]);
        Assert.Equal("8", fields[5]);
        Assert.Equal("7", fields[6]);
        Assert.Equal("19132", fields[10]);
        Assert.Equal("19132", fields[11]);
    }

    /// <summary>A semicolon in the MOTD would shift every field after it; a colour code shows as a character.</summary>
    [Fact]
    public void WhatTheOwnerWroteCannotBreakTheFields()
    {
        var hostile = Status with { Description = "a;b;c§k;\r\nMCPE;x", VersionName = "1;2" };

        var fields = RakNetWakeListener.BuildAdvertisement(hostile, guid: 7, port: 1).Split(';');

        Assert.Equal(13, fields.Length);   // twelve fields and the empty one after the last separator
        Assert.DoesNotContain('§', fields[1]);
    }

    [Fact]
    public void AVeryLongNameIsCutSoThePongStaysSmall()
    {
        var longName = Status with { Description = new string('x', 5000) };

        var pong = RakNetWakeListener.BuildPong(new byte[8], 1, longName, 19132);

        Assert.True(pong.Length < 300);
    }

    // --- what is answered and what is not ---

    private static RakNetWakeListener Listening(Action onJoin, out int port)
    {
        var listener = new RakNetWakeListener();
        port = FreeUdpPort();
        Assert.True(listener.Start(port, () => Status, onJoin));
        return listener;
    }

    [Theory]
    [InlineData(RakNetWakeListener.UnconnectedPing)]
    [InlineData(RakNetWakeListener.UnconnectedPingOpenConnections)]
    public void APingIsAnsweredAndWakesNothing(byte id)
    {
        var wakes = 0;
        using var listener = Listening(() => wakes++, out _);

        var reply = listener.Handle(Ping(id: id));

        Assert.NotNull(reply);
        Assert.Contains("Mi servidor", Advertisement(reply));
        Assert.Equal(0, wakes);
    }

    [Fact]
    public void AJoinRequestWakesTheServerAndIsNotAnswered()
    {
        var wakes = 0;
        using var listener = Listening(() => wakes++, out _);

        Assert.Null(listener.Handle(JoinRequest()));
        Assert.Equal(1, wakes);
    }

    /// <summary>A client repeats the request several times a second for as long as it waits.</summary>
    [Fact]
    public void ARepeatedJoinRequestWakesOnce()
    {
        var wakes = 0;
        using var listener = Listening(() => wakes++, out _);

        for (var i = 0; i < 12; i++) listener.Handle(JoinRequest(i % 2 == 0 ? 1464 : 1172));

        Assert.Equal(1, wakes);
    }

    [Fact]
    public void AJoinRequestWithoutThePaddingOfARealClientWakesNothing()
    {
        var wakes = 0;
        using var listener = Listening(() => wakes++, out _);

        listener.Handle(JoinRequest(size: 18));
        listener.Handle(JoinRequest(size: RakNetWakeListener.MinJoinRequestBytes - 1));

        Assert.Equal(0, wakes);
    }

    [Fact]
    public void WithoutTheMagicNothingIsAnsweredOrWoken()
    {
        var wakes = 0;
        using var listener = Listening(() => wakes++, out _);

        var ping = Ping();
        ping[12] ^= 0xFF;
        var join = JoinRequest();
        join[5] ^= 0xFF;

        Assert.Null(listener.Handle(ping));
        Assert.Null(listener.Handle(join));
        Assert.Equal(0, wakes);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x05 })]
    [InlineData(new byte[] { 0x1c, 1, 2, 3 })]
    [InlineData(new byte[] { 0xFE, 0x01 })]              // the legacy Java ping, sent at the wrong port
    [InlineData(new byte[] { 0x84, 0, 0, 0, 0, 0, 0 })]  // a connected RakNet frame: there is no connection
    public void GarbageGetsNothing(byte[] packet)
    {
        var wakes = 0;
        using var listener = Listening(() => wakes++, out _);

        Assert.Null(listener.Handle(packet));
        Assert.Equal(0, wakes);
    }

    /// <summary>
    /// The sender's address can be forged, so every answer is a packet somebody else may receive:
    /// they are rationed.
    /// </summary>
    [Fact]
    public void PongsAreRationed()
    {
        using var listener = Listening(() => { }, out _);

        var answered = 0;
        for (var i = 0; i < RakNetWakeListener.MaxPongsPerSecond * 5; i++)
            if (listener.Handle(Ping()) is not null) answered++;

        // Two windows at most, if the second ticked over half-way through the loop.
        Assert.InRange(answered, RakNetWakeListener.MaxPongsPerSecond, RakNetWakeListener.MaxPongsPerSecond * 2);
    }

    // --- on a real socket ---

    private static byte[]? Ask(int port, byte[] packet, int waitMs = 3000)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.ReceiveTimeout = waitMs;
        client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
        try
        {
            IPEndPoint? from = null;
            return client.Receive(ref from);
        }
        catch (SocketException) { return null; }
    }

    [Fact]
    public void OverUdpAPingGetsItsPongAndAJoinWakes()
    {
        using var woke = new ManualResetEventSlim();
        using var listener = Listening(woke.Set, out var port);

        var pong = Ask(port, Ping());
        Assert.NotNull(pong);
        Assert.Contains("Apagado", Advertisement(pong));

        Assert.Null(Ask(port, JoinRequest(), waitMs: 300));
        Assert.True(woke.Wait(3000));
    }

    [Fact]
    public void StoppingFreesThePortForGeyser()
    {
        var listener = Listening(() => { }, out var port);
        Assert.True(listener.IsListening);

        listener.Stop();

        Assert.False(listener.IsListening);
        using var geyser = new UdpClient(new IPEndPoint(IPAddress.Any, port));   // would throw if still held
        Assert.Null(Ask(port, Ping(), waitMs: 200));
    }

    [Fact]
    public void ABusyPortIsReportedNotThrown()
    {
        using var geyser = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)geyser.Client.LocalEndPoint!).Port;
        using var listener = new RakNetWakeListener();

        Assert.False(listener.Start(port, () => Status, () => { }));
        Assert.False(listener.IsListening);
    }

    /// <summary>The wake stops the listeners from the listener's own thread: it must not wait for itself.</summary>
    [Fact]
    public void AWakeThatStopsTheListenerDoesNotHang()
    {
        var listener = new RakNetWakeListener();
        var port = FreeUdpPort();
        using var stopped = new ManualResetEventSlim();
        Assert.True(listener.Start(port, () => Status, () => { listener.Stop(); stopped.Set(); }));

        Ask(port, JoinRequest(), waitMs: 200);

        Assert.True(stopped.Wait(5000));
    }

    // --- wired to a server ---

    [Fact]
    public void WithCrossplayARakNetJoinStartsTheServerAndStoppingFreesUdp()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var java = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var bedrock = FreeUdpPort();
        File.WriteAllText(Path.Combine(_folder, "server.properties"), $"server-port={java}\n");

        var starts = 0;
        using var started = new ManualResetEventSlim();
        var config = new ServerConfig
        {
            FolderPath = _folder,
            WakeOnDemand = true,
            CrossplayEnabled = true,
            BedrockPort = bedrock
        };
        using var controller = new WakeOnDemandController(config, new WakeHost(
            IsStarting: () => false,
            Motd: () => "Mi servidor",
            MaxPlayers: () => 8,
            CanStart: () => true,
            Say: _ => { },
            Notify: (_, _) => { },
            StartServer: () => { Interlocked.Increment(ref starts); started.Set(); },
            RunOnUi: action => action()), new ServerPropertiesService(), new WhitelistService(), new PlayersService());

        controller.Start();

        Assert.NotNull(Ask(bedrock, Ping()));
        Ask(bedrock, JoinRequest(), waitMs: 200);
        Assert.True(started.Wait(3000));
        Assert.Equal(1, starts);
        Assert.NotNull(controller.WokeAtUtc);

        controller.Stop();
        using var geyser = new UdpClient(new IPEndPoint(IPAddress.Any, bedrock));
    }
}
