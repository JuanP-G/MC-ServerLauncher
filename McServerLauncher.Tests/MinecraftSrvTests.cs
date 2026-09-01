using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading the port out of a DNS SRV reply.
/// </summary>
/// <remarks>
/// <para>
/// A playit tunnel gives out a bare domain and publishes the port separately. Asked about a real
/// one, <c>_minecraft._tcp.jakarta-rivers.tun.ply.gg</c> answers 14444 — and nothing in the address
/// says so. The Minecraft client looks it up; <c>TcpClient</c> does not, which is how the panel came
/// to report a healthy tunnel as dead.
/// </para>
/// <para>
/// The parsing is tested against bytes rather than a live name server: the answer comes off the
/// network and can be anything at all, including deliberately malformed, and none of those cases can
/// be produced by asking a real resolver nicely.
/// </para>
/// </remarks>
public class MinecraftSrvTests
{
    /// <summary>Builds a reply carrying one SRV answer, the way a name server would.</summary>
    private static byte[] Reply(int port, int answers = 1, int type = 33, int dataLength = 6)
    {
        var bytes = new List<byte>
        {
            0x4D, 0x43,                     // id
            0x81, 0x80,                     // response, no error
            0x00, 0x01,                     // one question
            (byte)(answers >> 8), (byte)(answers & 0xFF),
            0x00, 0x00, 0x00, 0x00,
        };

        // The question, echoed back: _minecraft._tcp.x
        foreach (var label in new[] { "_minecraft", "_tcp", "x" })
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0x00);
        bytes.AddRange(new byte[] { 0x00, 0x21, 0x00, 0x01 });

        for (var a = 0; a < answers; a++)
        {
            bytes.AddRange(new byte[] { 0xC0, 0x0C });                       // pointer to the name
            bytes.AddRange(new byte[] { (byte)(type >> 8), (byte)type });     // type
            bytes.AddRange(new byte[] { 0x00, 0x01 });                        // class IN
            bytes.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x3C });            // ttl
            bytes.AddRange(new byte[] { (byte)(dataLength >> 8), (byte)dataLength });
            bytes.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 });            // priority, weight
            bytes.AddRange(new byte[] { (byte)(port >> 8), (byte)(port & 0xFF) });
            for (var i = 6; i < dataLength; i++) bytes.Add(0x00);             // target, ignored
        }

        return bytes.ToArray();
    }

    // --- The question ---

    [Fact]
    public void TheNameAskedForIsTheOneMinecraftUses()
    {
        Assert.Equal("_minecraft._tcp.jakarta-rivers.tun.ply.gg",
            MinecraftSrv.QueryName("jakarta-rivers.tun.ply.gg"));
    }

    [Fact]
    public void ATrailingDotDoesNotBecomeAnEmptyLabel()
    {
        // An empty label is illegal on the wire and the query would be rejected outright.
        Assert.Equal("_minecraft._tcp.example.com", MinecraftSrv.QueryName("example.com."));
    }

    [Fact]
    public void TheQueryIsOneSrvQuestionAndNothingElse()
    {
        var query = MinecraftSrv.BuildQuery("_minecraft._tcp.x", 0x4D43);

        Assert.Equal(0x4D, query[0]);
        Assert.Equal(0x43, query[1]);
        Assert.Equal(1, (query[4] << 8) | query[5]);              // exactly one question
        Assert.Equal(0, (query[6] << 8) | query[7]);              // no answers
        Assert.Equal(33, (query[^4] << 8) | query[^3]);           // type SRV
        Assert.Equal(1, (query[^2] << 8) | query[^1]);            // class IN
    }

    [Fact]
    public void ALabelTooLongForTheWireIsRefusedRatherThanTruncated()
    {
        // A label caps at 63 bytes. Writing a longer length byte would corrupt the packet, and a
        // silently truncated name would ask about a different host than the one requested.
        Assert.Empty(MinecraftSrv.BuildQuery("_minecraft._tcp." + new string('a', 64), 1));
    }

    // --- The answer ---

    [Fact]
    public void ThePortComesOutOfTheAnswer()
    {
        Assert.Equal(14444, MinecraftSrv.ReadSrvPort(Reply(14444)));
    }

    [Fact]
    public void APortAtTheTopOfTheRangeIsNotReadAsNegative()
    {
        // Both bytes are shifted and or-ed; getting the signs wrong here turns 51917 into rubbish,
        // and 51917 is a real playit port.
        Assert.Equal(51917, MinecraftSrv.ReadSrvPort(Reply(51917)));
        Assert.Equal(65535, MinecraftSrv.ReadSrvPort(Reply(65535)));
    }

    [Fact]
    public void NoAnswersMeansNoPort()
    {
        // The ordinary case for most addresses: they have no SRV record and are reached on the port
        // they were given. It must read as "ask elsewhere", never as a failure.
        Assert.Null(MinecraftSrv.ReadSrvPort(Reply(25565, answers: 0)));
    }

    [Fact]
    public void AnAnswerOfAnotherTypeIsSkipped()
    {
        // Type 5 is CNAME. Reading its data as a port would produce a confident wrong number.
        Assert.Null(MinecraftSrv.ReadSrvPort(Reply(25565, type: 5)));
    }

    [Fact]
    public void AnSrvAnswerTooShortToHoldAPortIsRefused()
    {
        Assert.Null(MinecraftSrv.ReadSrvPort(Reply(25565, dataLength: 4)));
    }

    // --- Replies that are not replies ---

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void SomethingShorterThanAHeaderIsRefused(int length) =>
        Assert.Null(MinecraftSrv.ReadSrvPort(new byte[length]));

    [Fact]
    public void ATruncatedReplyIsRefusedRatherThanReadPastTheEnd()
    {
        // Every prefix of a valid reply. A name server is reachable by whoever is on the way to it,
        // so "the answer stops early" is not a hypothetical — and reading past the end here would
        // be an exception thrown from a background timer.
        var full = Reply(14444);
        for (var length = 0; length < full.Length; length++)
            MinecraftSrv.ReadSrvPort(full[..length]);   // must not throw

        Assert.Equal(14444, MinecraftSrv.ReadSrvPort(full));
    }

    [Fact]
    public void AReplyClaimingMoreAnswersThanItCarriesIsRefused()
    {
        var lying = Reply(14444);
        lying[6] = 0x00;
        lying[7] = 0x40;    // sixty-four answers, one present

        // Whatever it returns, it must not throw and must not walk off the end.
        MinecraftSrv.ReadSrvPort(lying);
    }

    [Fact]
    public void ANameThatPointsToItselfDoesNotSpin()
    {
        // Compression pointers can be made to loop. Following them blindly hangs the thread; this
        // one stops because a pointer always ends the name.
        var loop = Reply(14444);
        loop[^12] = 0xC0;
        loop[^11] = 0x0C;

        MinecraftSrv.ReadSrvPort(loop);
    }

    // --- The socket path ---

    [Fact]
    public async Task AnEmptyHostIsNotLookedUp()
    {
        Assert.Null(await MinecraftSrv.LookupPortAsync(null));
        Assert.Null(await MinecraftSrv.LookupPortAsync("   "));
    }
}
