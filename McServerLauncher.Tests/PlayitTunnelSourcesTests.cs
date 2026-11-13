using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Tests;

/// <summary>
/// A server's tunnel can live on either of the two Playit agents this machine knows (the app's own
/// and the one the user installed), and <c>/agents/rundata</c> answers for one agent at a time.
/// </summary>
/// <remarks>
/// The tunnels screen asked both, the server card and the tunnel creation asked one. A tunnel on the
/// other agent was therefore on the account, with an address and a server, and invisible to the card
/// that said "no tunnel for this server" — while starting the server made yet another one on the
/// same port, because the creation did not see it either.
/// </remarks>
public class PlayitTunnelSourcesTests
{
    private static PlayitTunnel Tcp(string id, int localPort, string name = "t") =>
        new(id, name, localPort, id + ".tun.example", null, "tcp", 0);

    private static Task<List<PlayitTunnel>> Answer(params PlayitTunnel[] tunnels) =>
        Task.FromResult(tunnels.ToList());

    [Fact]
    public async Task ATunnelOnEitherAgentIsFound()
    {
        // The installed agent holds "multiplatform" on 25566; the app's agent holds nothing.
        var merged = await FetchFromEveryKeyAsync(["app", "installed"],
            key => key == "installed" ? Answer(Tcp("a", 25566, "multiplatform")) : Answer());

        var tunnel = Match(merged, 25566, udp: false);

        Assert.NotNull(tunnel);
        Assert.Equal("multiplatform", tunnel!.Name);
    }

    [Fact]
    public async Task TheCardForAServerShowsTheAddressOfATunnelOnTheOtherAgent()
    {
        var merged = await FetchFromEveryKeyAsync(["app", "installed"],
            key => key == "installed" ? Answer(Tcp("a", 25566)) : Answer());

        var shown = TunnelAddressSync.Java(merged, 25566);

        Assert.Equal(TunnelAddressState.Ready, shown!.Value.State);
        Assert.Equal("a.tun.example", shown.Value.Address);
    }

    [Fact]
    public async Task EveryAgentIsAskedWithItsOwnKey()
    {
        var asked = new List<string>();

        await FetchFromEveryKeyAsync(["app", "installed"], key =>
        {
            lock (asked) asked.Add(key);
            return Answer();
        });

        Assert.Equal(["app", "installed"], asked.Order());
    }

    [Fact]
    public async Task ATunnelBothAgentsReportIsOneTunnel()
    {
        var merged = await FetchFromEveryKeyAsync(["app", "installed"], _ => Answer(Tcp("same", 25565)));

        Assert.Single(merged);
    }

    [Fact]
    public async Task AKeyPlayitRefusesDoesNotHideTheOthers()
    {
        // A stale playit.toml: the agent was removed, its key no longer opens anything.
        var merged = await FetchFromEveryKeyAsync(["app", "stale"], key =>
            key == "stale"
                ? throw new PlayitApiException("auth", "InvalidAgentKey")
                : Answer(Tcp("a", 25565)));

        Assert.Single(merged);
    }

    [Fact]
    public async Task IfEveryKeyIsRefusedTheAccountWasNotAsked()
    {
        await Assert.ThrowsAsync<PlayitApiException>(() => FetchFromEveryKeyAsync(["a", "b"],
            _ => throw new PlayitApiException("auth", "InvalidAgentKey")));
    }

    [Fact]
    public async Task AnAgentThatCannotBeReachedIsNotTreatedAsHavingNoTunnels()
    {
        // The other agent answering "nothing" would make a tunnel that lives on the unreachable one
        // look absent: the card would say "no tunnel" and a second one would be made. Not knowing is
        // reported as not knowing.
        await Assert.ThrowsAsync<HttpRequestException>(() => FetchFromEveryKeyAsync(["app", "installed"],
            key => key == "installed" ? throw new HttpRequestException("offline") : Answer()));
    }

    [Fact]
    public void TheSameKeyTwiceIsOneAgentAndBlankOnesAreNone()
    {
        Assert.Equal(["x"], DistinctKeys("x", "x"));
        Assert.Equal(["x"], DistinctKeys(null, "x", " ", ""));
        Assert.Empty(DistinctKeys(null, null));
    }
}
