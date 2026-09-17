using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The two tunnels a crossplay server needs.
/// </summary>
/// <remarks>
/// Java is TCP and Bedrock is UDP — different protocols, so one tunnel cannot serve both. The
/// identifiers below are playit's own, read out of their agent's source. Getting one wrong creates
/// a tunnel that appears perfectly healthy in their dashboard and carries no traffic, which is the
/// kind of mistake that costs an evening.
/// </remarks>
public class PlayitTunnelTests
{
    [Fact]
    public void JavaIsTcp()
    {
        var (type, port) = PlayitApiService.Wire(PlayitApiService.TunnelEdition.Java);

        Assert.Equal("minecraft-java", type);
        Assert.Equal("tcp", port);
    }

    [Fact]
    public void BedrockIsUdp()
    {
        var (type, port) = PlayitApiService.Wire(PlayitApiService.TunnelEdition.Bedrock);

        Assert.Equal("minecraft-bedrock", type);
        Assert.Equal("udp", port);
    }

    [Fact]
    public void TheTwoEditionsAreNotTheSameTunnel()
    {
        // A copy-paste that left Bedrock on "tcp" would produce a tunnel that carries nothing.
        Assert.NotEqual(PlayitApiService.Wire(PlayitApiService.TunnelEdition.Java),
                        PlayitApiService.Wire(PlayitApiService.TunnelEdition.Bedrock));
    }

    // --- telling the two apart once they exist ---

    [Fact]
    public void AUdpTunnelIsRecognisedAsTheBedrockOne()
    {
        var java = new PlayitApiService.PlayitTunnel("1", "srv", 25565, "a.example", null, "tcp", 51000);
        var bedrock = new PlayitApiService.PlayitTunnel("2", "srv", 19132, "a.example", null, "udp", 51001);

        Assert.False(java.IsUdp);
        Assert.True(bedrock.IsUdp);
    }

    [Fact]
    public void ProtocolComparisonIsNotCaseSensitive()
    {
        // The API's casing is not ours to depend on, and getting this wrong would make the app
        // think a crossplay server had no Bedrock tunnel and create a second one every start.
        var tunnel = new PlayitApiService.PlayitTunnel("1", "srv", 19132, "a.example", null, "UDP", 51001);

        Assert.True(tunnel.IsUdp);
    }

    [Fact]
    public void TheSamePortOnTheOtherProtocolIsADifferentTunnel()
    {
        // Deleting used to match on the port alone while the lookup matched on port and protocol,
        // so removing a crossplay server could delete a tunnel of the account that merely shared
        // the number. Both go through one definition now, and deleting a tunnel is not undoable.
        var tunnels = new List<PlayitApiService.PlayitTunnel>
        {
            new("1", "otro", 19132, "a.example", null, "tcp", 51000),
            new("2", "mio",  19132, "b.example", null, "udp", 51001),
        };

        Assert.Equal("2", PlayitApiService.Match(tunnels, 19132, udp: true)!.Id);
        Assert.Equal("1", PlayitApiService.Match(tunnels, 19132, udp: false)!.Id);

        // And nothing at all rather than the wrong one when this protocol has no tunnel there.
        Assert.Null(PlayitApiService.Match(tunnels, 25565, udp: true));
    }

    [Fact]
    public void ACustomDomainWinsOverTheAssignedOne()
    {
        var tunnel = new PlayitApiService.PlayitTunnel(
            "1", "srv", 19132, "auto.playit.gg", "mio.example.com", "udp", 51001);

        Assert.Equal("mio.example.com", tunnel.Address);
    }

    [Fact]
    public void ExistingTunnelsKeepWorkingWithoutTheNewFields()
    {
        // Proto and PublicPort are optional so nothing that built a PlayitTunnel before has to
        // change; the default has to be the Java one, which is what every existing tunnel is.
        var tunnel = new PlayitApiService.PlayitTunnel("1", "srv", 25565, "a.example", null);

        Assert.False(tunnel.IsUdp);
        Assert.Equal("tcp", tunnel.Proto);
    }

    [Fact]
    public void TheJavaLookupIgnoresTheUdpTunnel()
    {
        // There used to be an address-only lookup beside GetTunnelAsync that took the first tunnel
        // on the port without looking at the protocol. A crossplay server has two on ports that can
        // coincide, so the box labelled Java could hand the player the Bedrock address: a
        // plausible-looking answer that simply does not connect, and nothing on screen to suggest
        // which of the two you were reading.
        var tunnels = new List<PlayitApiService.PlayitTunnel>
        {
            new("1", "srv (Bedrock)", 25565, "bedrock.example", null, "udp", 51001),
            new("2", "srv",           25565, "java.example",    null, "tcp", 51000),
        };

        Assert.Equal("java.example", PlayitApiService.Match(tunnels, 25565, udp: false)!.Address);
    }

    [Fact]
    public void EveryTunnelLookupGoesThroughMatch()
    {
        // Match calls itself "the one definition of the same tunnel", and it had already drifted
        // once. Saying so in a comment did not stop the address lookup being written without it,
        // so the promise is checked against the file instead: any new "LocalPort ==" outside Match
        // is a second definition, and second definitions are how this bug got here.
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Services", "PlayitApiService.cs"));

        var occurrences = source.Split("LocalPort ==").Length - 1;

        Assert.True(occurrences == 1,
            $"«LocalPort ==» aparece {occurrences} veces en PlayitApiService.cs: la comparación " +
            "vive en Match y los demás la llaman, no la repiten.");
    }

    [Fact]
    public void TheRetryDelaysGrowAndStop()
    {
        var delays = AddressRetry.DelaysSeconds;

        Assert.NotEmpty(delays);
        Assert.All(delays, d => Assert.True(d > 0));

        for (var i = 1; i < delays.Length; i++)
            Assert.True(delays[i] > delays[i - 1],
                "los reintentos tienen que espaciarse: si no, son una ráfaga de peticiones iguales");

        // Long enough to cover the wait that actually happens, short enough that it ends and hands
        // over to the ordinary refresh instead of polling the API for the rest of the session.
        var total = delays.Sum();
        Assert.InRange(total, 20, 60);
    }

    [Fact]
    public void WithoutAskingForFreshDataMostOfTheBurstAnsweredItself()
    {
        // The finding this fix exists for, in numbers. The burst fires at +2, +5, +10, +18 and +31
        // seconds; the shared list is cached for 25. So the first attempt paid for a request and
        // cached what came back — which, moments after the tunnel was created, is an empty list —
        // and the next three were handed that same emptiness without a request leaving the machine.
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastFetch = DateTime.MinValue;
        var reachedTheApi = 0;
        var elapsed = 0;

        foreach (var seconds in AddressRetry.DelaysSeconds)
        {
            elapsed += seconds;
            var now = start.AddSeconds(elapsed);
            if (PlayitApiService.ShouldFetchTunnels(fresh: false, lastFetch, now))
            {
                reachedTheApi++;
                lastFetch = now;
            }
        }

        Assert.Equal(2, reachedTheApi);
    }

    [Fact]
    public void AskingForFreshDataMakesEveryAttemptCount()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var lastFetch = DateTime.MinValue;
        var reachedTheApi = 0;
        var elapsed = 0;

        foreach (var seconds in AddressRetry.DelaysSeconds)
        {
            elapsed += seconds;
            var now = start.AddSeconds(elapsed);
            if (PlayitApiService.ShouldFetchTunnels(fresh: true, lastFetch, now))
            {
                reachedTheApi++;
                lastFetch = now;
            }
        }

        Assert.Equal(AddressRetry.DelaysSeconds.Length, reachedTheApi);
    }

    [Fact]
    public void TheOrdinaryRefreshStillSharesOneFetchBetweenEveryServer()
    {
        // What fresh must not cost: the cache is there so that N servers refreshing every 30
        // seconds make one call between them, not N. Only the burst asks to skip it.
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fetchedAt = now.AddSeconds(-5);

        Assert.False(PlayitApiService.ShouldFetchTunnels(fresh: false, fetchedAt, now));

        // And that it still expires, or a tunnel deleted elsewhere would linger on screen.
        Assert.True(PlayitApiService.ShouldFetchTunnels(
            fresh: false, now - PlayitApiService.TunnelCacheTtl, now));
    }
}
