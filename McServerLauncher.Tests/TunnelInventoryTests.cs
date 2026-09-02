using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using static McServerLauncher.Services.PlayitApiService;
using static McServerLauncher.Services.TunnelInventory;

namespace McServerLauncher.Tests;

/// <summary>
/// Crossing the tunnels on the account against the servers this app knows about.
/// </summary>
/// <remarks>
/// The two halves existed and never met: the account has always been able to list every tunnel, and
/// the app has known which server owns which port since 1.11.2. What was missing was anywhere to
/// see the answer — so a tunnel left behind by a deleted server showed up nowhere, and a collision
/// was only ever met as "it does not connect".
/// </remarks>
public class TunnelInventoryTests
{
    private static PlayitTunnel Tcp(int localPort, string name = "t") =>
        new(name, name, localPort, name + ".gl.joinmc.link", null);

    private static PlayitTunnel Udp(int localPort, string name = "u") =>
        new(name, name, localPort, name + ".gl.joinmc.link", null, "udp", 19132);

    private static ServerPorts Server(string name, int? java = null, int bedrock = 0) =>
        new(name, java, bedrock);

    // --- Ownership ---

    [Fact]
    public void ATunnelOnAServersJavaPortBelongsToIt()
    {
        var rows = Build(new[] { Tcp(25565) }, new[] { Server("Supervivencia", java: 25565) });

        Assert.Equal("Supervivencia", rows[0].Owner);
        Assert.Equal(TunnelHealth.InUse, rows[0].Health);
    }

    [Fact]
    public void ABedrockTunnelBelongsToTheServerHoldingThatUdpPort()
    {
        var rows = Build(new[] { Udp(19133) }, new[] { Server("Supervivencia", java: 25565, bedrock: 19133) });

        Assert.Equal("Supervivencia", rows[0].Owner);
        Assert.True(rows[0].IsBedrock);
    }

    [Fact]
    public void AJavaServerDoesNotAdoptSomebodyElsesBedrockTunnel()
    {
        // The number matches and nothing else does. Matching on the port alone would report a real
        // orphan as healthy, which is the one answer this panel exists to give.
        var rows = Build(new[] { Udp(25565) }, new[] { Server("Supervivencia", java: 25565) });

        Assert.Null(rows[0].Owner);
        Assert.Equal(TunnelHealth.Orphan, rows[0].Health);
    }

    [Fact]
    public void AServerWithoutCrossplayHoldsNoUdpPort()
    {
        // BedrockPort is zero when crossplay was never set up. Treating zero as a port would let a
        // server claim a tunnel on port 0 that cannot exist.
        var rows = Build(new[] { Udp(0) }, new[] { Server("Supervivencia", java: 25565, bedrock: 0) });

        Assert.Null(rows[0].Owner);
    }

    [Fact]
    public void AServerWhoseJavaPortCannotBeReadOwnsNothing()
    {
        // server.properties can be missing or unreadable. "I do not know the port" must not become
        // "the port is anything you like".
        var rows = Build(new[] { Tcp(25565) }, new[] { Server("Supervivencia", java: null) });

        Assert.Null(rows[0].Owner);
        Assert.Equal(TunnelHealth.Orphan, rows[0].Health);
    }

    // --- Orphans ---

    [Fact]
    public void ATunnelNoServerListensOnIsAnOrphan()
    {
        var rows = Build(new[] { Tcp(25567) }, new[] { Server("Supervivencia", java: 25565) });

        Assert.Equal(TunnelHealth.Orphan, rows[0].Health);
        Assert.True(rows[0].NeedsAttention);
    }

    [Fact]
    public void WithNoServersAtAllEverythingIsAnOrphan()
    {
        var rows = Build(new[] { Tcp(25565), Udp(19133) }, Array.Empty<ServerPorts>());

        Assert.All(rows, r => Assert.Equal(TunnelHealth.Orphan, r.Health));
    }

    // --- Collisions ---

    [Fact]
    public void TwoTunnelsOnTheSameUdpPortBothClash()
    {
        // The 1.11.2 bug, made visible: creating a tunnel on a port that already has one does not
        // fail, it silently reports the existing one. Only one of the two can actually carry traffic.
        var rows = Build(
            new[] { Udp(19133, "a"), Udp(19133, "b") },
            new[] { Server("Supervivencia", bedrock: 19133) });

        Assert.All(rows, r => Assert.Equal(TunnelHealth.PortClash, r.Health));
    }

    [Fact]
    public void TcpAndUdpOnTheSameNumberAreNotAClash()
    {
        // This is what an ordinary crossplay server looks like. Flagging it would make the panel cry
        // wolf on a perfectly healthy pair — the same mistake Match was written to stop the delete
        // path making.
        var rows = Build(
            new[] { Tcp(19132, "java"), Udp(19132, "bedrock") },
            new[] { Server("Supervivencia", java: 19132, bedrock: 19132) });

        Assert.All(rows, r => Assert.Equal(TunnelHealth.InUse, r.Health));
    }

    [Fact]
    public void AClashIsReportedEvenWhenAServerDoesOwnThePort()
    {
        // Owned and broken at once: something has to be deleted or moved, and calling it healthy
        // because a server is on that port would hide exactly the case worth showing.
        var rows = Build(
            new[] { Udp(19133, "mine"), Udp(19133, "stray") },
            new[] { Server("Supervivencia", bedrock: 19133) });

        Assert.Equal(2, AttentionCount(rows));
    }

    // --- Counting ---

    [Fact]
    public void OnlyTheBrokenOnesAreCounted()
    {
        var rows = Build(
            new[] { Tcp(25565, "ok"), Tcp(25567, "huerfano"), Udp(19133, "a"), Udp(19133, "b") },
            new[] { Server("Supervivencia", java: 25565, bedrock: 19133) });

        // The healthy one, and only it, stays out of the count.
        Assert.Equal(3, AttentionCount(rows));
        Assert.Single(rows, r => !r.NeedsAttention);
    }

    [Fact]
    public void NoTunnelsIsNotAProblem()
    {
        var rows = Build(Array.Empty<PlayitTunnel>(), new[] { Server("Supervivencia", java: 25565) });

        Assert.Empty(rows);
        Assert.Equal(0, AttentionCount(rows));
    }

    [Fact]
    public void TheAccountsOrderIsKept()
    {
        // The list is what the account returned. Re-sorting it would move rows under the user
        // between refreshes for no reason they could see.
        var rows = Build(
            new[] { Tcp(25567, "tercero"), Tcp(25565, "primero"), Udp(19133, "segundo") },
            new[] { Server("Supervivencia", java: 25565, bedrock: 19133) });

        Assert.Equal(new[] { "tercero", "primero", "segundo" },
            rows.Select(r => r.Tunnel.Name).ToArray());
    }

    // --- The view actually binds to things that exist ---

    [Fact]
    public void EveryNameTheTunnelsPanelBindsToExists()
    {
        // TunnelsView is x:CompileBindings="False". A name that is not there raises nothing at all:
        // the cell simply renders empty, and an empty cell in a table about broken tunnels reads as
        // "nothing is wrong". This is the only thing standing between a typo and that.
        var xaml = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "TunnelsView.axaml"));

        var start = xaml.IndexOf("x:DataType=\"vm:TunnelRowViewModel\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "la plantilla de fila ya no está en TunnelsView.axaml");
        var end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        var row = xaml[start..end];

        foreach (Match m in Regex.Matches(row, @"\{Binding ([A-Za-z][A-Za-z0-9_]*)[,}]"))
        {
            var name = m.Groups[1].Value;
            if (name.StartsWith("DataContext", StringComparison.Ordinal)) continue;

            Assert.True(
                typeof(TunnelRowViewModel).GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance) is not null,
                $"TunnelRowViewModel no tiene ninguna propiedad «{name}», que TunnelsView.axaml enlaza");
        }
    }

    [Fact]
    public void VisibilityIsNeverBoundToACount()
    {
        // The panel once said IsVisible="{Binding !TunnelsAttention}", negating an int. With
        // compiled bindings off that does not fail — it quietly decides something, and a green dot
        // that is always lit says "all good" over three rows saying otherwise.
        var xaml = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "TunnelsView.axaml"));

        var offenders = Regex.Matches(xaml, @"IsVisible=""\{Binding !?([A-Za-z][A-Za-z0-9_.]*)\}""")
            .Select(m => m.Groups[1].Value)
            .Where(n => !n.Contains('.'))
            .Where(n =>
            {
                var p = typeof(MainViewModel).GetProperty(n, BindingFlags.Public | BindingFlags.Instance)
                     ?? typeof(TunnelRowViewModel).GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                return p is not null && p.PropertyType != typeof(bool);
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "IsVisible enlazado a algo que no es bool: " + string.Join(", ", offenders));
    }
}
