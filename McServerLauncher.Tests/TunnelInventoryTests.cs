using McServerLauncher.Services;
using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading the account's tunnels against the servers on this machine.
/// </summary>
public class TunnelInventoryTests
{
    private static PlayitTunnel Tcp(string id, string name, int port) =>
        new(id, name, port, id + ".joinmc.link", null, "tcp", 30000 + port % 1000);

    private static PlayitTunnel Udp(string id, string name, int port) =>
        new(id, name, port, id + ".ply.gg", null, "udp", 40000 + port % 1000);

    private static ServerPorts Server(string name, int? java, int? bedrock = null, bool playit = true) =>
        new(name.ToLowerInvariant(), name, java, bedrock, playit);

    private static TunnelReport Report(IReadOnlyList<PlayitTunnel> tunnels, params ServerPorts[] servers) =>
        TunnelInventory.Build(tunnels, servers);

    // --- health ---

    [Fact]
    public void ATunnelOnAServersPortIsFine()
    {
        var report = Report([Tcp("a", "Survival", 25565)], Server("Survival", 25565));

        Assert.Equal(TunnelHealth.Ok, report.Rows.Single().Health);
        Assert.Empty(report.Suggestions);
        Assert.Equal(0, report.ProblemCount);
    }

    [Fact]
    public void ATunnelNoServerListensOnIsAnOrphanAndSuggestsDeletingIt()
    {
        var report = Report([Tcp("a", "viejo", 25570)], Server("Survival", 25565));

        Assert.Equal(TunnelHealth.Orphan, report.Rows.Single().Health);
        var s = report.Suggestions.Single(x => x.Kind == TunnelSuggestionKind.DeleteOrphan);
        Assert.Equal("a", s.TunnelId);
        Assert.Equal("viejo", s.Subject);
    }

    [Fact]
    public void TwoServersOnOnePortAreFlaggedOnceAndTheOneNotNamedAfterTheTunnelIsToMove()
    {
        var report = Report([Tcp("a", "Survival", 25565)],
            Server("Survival", 25565), Server("Creativo", 25565));

        Assert.Equal(TunnelHealth.Shared, report.Rows.Single().Health);
        var s = report.Suggestions.Single(x => x.Kind == TunnelSuggestionKind.SharedPort);
        Assert.Equal("Creativo", s.Subject);   // the tunnel is "Survival", so Survival keeps the port
        Assert.Equal("Survival", s.Other);
        Assert.Equal("creativo", s.ServerId);
    }

    [Fact]
    public void ATunnelExistingTwiceKeepsTheOneNamedAfterItsServer()
    {
        var report = Report([Tcp("a", "mc-25565", 25565), Tcp("b", "Survival", 25565)], Server("Survival", 25565));

        Assert.All(report.Rows, r => Assert.Equal(TunnelHealth.Duplicate, r.Health));
        var s = report.Suggestions.Single(x => x.Kind == TunnelSuggestionKind.DeleteDuplicate);
        Assert.Equal("a", s.TunnelId);   // the oddly-named one goes, the tidy one stays
    }

    [Fact]
    public void WithNoTidyCopyTheFirstOneIsKept()
    {
        var report = Report([Tcp("a", "x", 25565), Tcp("b", "y", 25565)], Server("Survival", 25565));

        Assert.Equal("b", report.Suggestions.Single(x => x.Kind == TunnelSuggestionKind.DeleteDuplicate).TunnelId);
    }

    // --- protocol ---

    [Fact]
    public void ATcpTunnelBelongsToTheJavaPortAndNotTheBedrockOne()
    {
        // Java 25565, Bedrock 19132. A TCP tunnel on 19132 belongs to neither server's Java side.
        var report = Report([Tcp("a", "x", 19132)], Server("Survival", 25565, 19132));

        Assert.Equal(TunnelHealth.Orphan, report.Rows.Single().Health);
    }

    [Fact]
    public void AUdpTunnelBelongsToTheBedrockPortAndNotTheJavaOne()
    {
        var report = Report([Udp("a", "x", 25565)], Server("Survival", 25565, 19132));

        Assert.Equal(TunnelHealth.Orphan, report.Rows.Single().Health);
    }

    [Fact]
    public void WithCrossplayOffAUdpTunnelOnTheOldBedrockPortIsAnOrphan()
    {
        // BedrockPort is null in the snapshot when crossplay is off.
        var report = Report([Udp("a", "Survival (Bedrock)", 19132)], Server("Survival", 25565, bedrock: null));

        Assert.Equal(TunnelHealth.Orphan, report.Rows.Single().Health);
    }

    [Fact]
    public void SameNumberOverTcpAndUdpIsTwoTunnelsNotADuplicate()
    {
        var report = Report([Tcp("a", "Survival", 25565), Udp("b", "Survival (Bedrock)", 25565)],
            Server("Survival", 25565, 25565));

        Assert.All(report.Rows, r => Assert.Equal(TunnelHealth.Ok, r.Health));
    }

    // --- what is missing ---

    [Fact]
    public void AServerMeantForPlayitWithNoTunnelIsOfferedOne()
    {
        var report = Report([], Server("Survival", 25565));

        var s = Assert.Single(report.Suggestions);
        Assert.Equal(TunnelSuggestionKind.CreateJava, s.Kind);
        Assert.Equal("survival", s.ServerId);
    }

    [Fact]
    public void AServerNotUsingPlayitIsNotNaggedAboutATunnel()
    {
        var report = Report([], Server("Survival", 25565, 19132, playit: false));

        Assert.Empty(report.Suggestions);
    }

    [Fact]
    public void ACrossplayServerWithoutItsUdpTunnelIsOfferedOne()
    {
        var report = Report([Tcp("a", "Survival", 25565)], Server("Survival", 25565, 19132));

        var s = Assert.Single(report.Suggestions);
        Assert.Equal(TunnelSuggestionKind.CreateBedrock, s.Kind);
    }

    [Fact]
    public void AServerWhosePortCannotBeReadIsNotOfferedATunnel()
    {
        var report = Report([], Server("Survival", java: null));

        Assert.Empty(report.Suggestions);
    }

    // --- names ---

    [Fact]
    public void NamesFollowTheFormatTunnelsAreCreatedUnder()
    {
        Assert.Equal("Survival", TunnelInventory.NameFor("Survival", udp: false));
        Assert.Equal("Survival (Bedrock)", TunnelInventory.NameFor("Survival", udp: true));
    }

    [Fact]
    public void AWorkingTunnelWithAnotherNameIsSuggestedARenameButIsNotAProblem()
    {
        var report = Report([Tcp("a", "mc-25565-tcp", 25565), Udp("b", "mc-19132", 19132)],
            Server("Survival", 25565, 19132));

        Assert.Equal(0, report.ProblemCount);
        Assert.Equal("Survival", report.Rows[0].SuggestedName);
        Assert.Equal("Survival (Bedrock)", report.Rows[1].SuggestedName);
        Assert.True(report.Rows.All(r => r.NameDiffers));

        var s = Assert.Single(report.Suggestions);
        Assert.Equal(TunnelSuggestionKind.RenameAll, s.Kind);
        Assert.Equal(2, s.Count);
        Assert.False(s.IsProblem);
    }

    [Fact]
    public void NoNameIsSuggestedWhereThereIsNoSingleOwner()
    {
        var report = Report([Tcp("a", "x", 25565), Tcp("b", "y", 25570)],
            Server("A", 25565), Server("B", 25565));

        Assert.All(report.Rows, r => Assert.Null(r.SuggestedName));
    }

    // --- following a renamed server ---

    [Fact]
    public void RenamingAServerRenamesTheTunnelsItsOldNameWasGivenTo()
    {
        var server = Server("Nuevo", 25565, 19132);
        var tunnels = new[] { Tcp("a", "Viejo", 25565), Udp("b", "Viejo (Bedrock)", 19132) };

        var renames = TunnelInventory.RenamesFor(server, "Viejo", tunnels, [server]);

        Assert.Equal([("a", "Nuevo"), ("b", "Nuevo (Bedrock)")], renames);
    }

    [Fact]
    public void ATunnelNamedByHandIsLeftAlone()
    {
        var server = Server("Nuevo", 25565);
        var tunnels = new[] { Tcp("a", "el-de-mi-amigo", 25565) };

        Assert.Empty(TunnelInventory.RenamesFor(server, "Viejo", tunnels, [server]));
    }

    [Fact]
    public void ATunnelThatIsAnotherServersIsNeverRenamed()
    {
        var mine = Server("Nuevo", 25565);
        var other = Server("Viejo", 25570);
        // Named "Viejo" and pointing at the other server's port: nothing to do with `mine`.
        var tunnels = new[] { Tcp("a", "Viejo", 25570) };

        Assert.Empty(TunnelInventory.RenamesFor(mine, "Viejo", tunnels, [mine, other]));
    }

    [Fact]
    public void ASharedTunnelIsNotRenamedBecauseNoOneOwnsIt()
    {
        var a = Server("Nuevo", 25565);
        var b = Server("Otro", 25565);
        var tunnels = new[] { Tcp("t", "Viejo", 25565) };

        Assert.Empty(TunnelInventory.RenamesFor(a, "Viejo", tunnels, [a, b]));
    }

    [Fact]
    public void NothingToRenameWhenTheNameDidNotChange()
    {
        var server = Server("Igual", 25565);
        var tunnels = new[] { Tcp("a", "Igual", 25565) };

        Assert.Empty(TunnelInventory.RenamesFor(server, "Igual", tunnels, [server]));
    }

    // --- the wire format ---

    [Fact]
    public void TheRenameBodyUsesPlayitsFieldNames()
    {
        // tunnel_id and name, as in playit's own client (ReqTunnelsRename).
        Assert.Equal("""{"tunnel_id":"abc","name":"Mi servidor"}""", PlayitApiService.RenameBody("abc", "Mi servidor"));
    }

    [Fact]
    public void TheDeleteBodyNamesTheTunnelById() =>
        Assert.Equal("""{"tunnel_id":"abc"}""", PlayitApiService.DeleteBody("abc"));
}
