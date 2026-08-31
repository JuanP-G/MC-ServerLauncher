using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Choosing the local UDP port for Geyser, and saying what is happening while there is no address.
/// </summary>
/// <remarks>
/// <para>
/// Two reports came out of the same area and neither had a test. "The Bedrock port does not appear"
/// was three different situations rendered as one blank panel, and "servers get tunnels on ports
/// already in use" was a port search that could only see what was bound on this machine at that
/// instant — blind to the tunnels on the playit account, which is precisely where the collisions
/// were coming from.
/// </para>
/// <para>
/// The port search uses real sockets, like <see cref="UdpPortTests"/>: the whole question is what
/// the operating system reports.
/// </para>
/// </remarks>
public class BedrockPortTests
{
    private static readonly int[] None = Array.Empty<int>();

    // --- Which port gets picked ---

    [Fact]
    public void APortHeldByATunnelOnTheAccountIsNotHandedOut()
    {
        // The port is free on this machine — nothing is bound to it — and no other server in the
        // app holds it. Only the account knows it is taken, which is the case that was being
        // missed: creating a tunnel there does not fail, it silently adopts the existing one.
        var port = new CrossplayService().PickBedrockPort(
            None,
            new[] { CrossplayService.DefaultBedrockPort });

        Assert.NotEqual(CrossplayService.DefaultBedrockPort, port);
    }

    [Fact]
    public void AllThreeSourcesAreAvoidedAtOnce()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bound = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        var chosen = new CrossplayService().PickBedrockPort(
            new[] { CrossplayService.DefaultBedrockPort },
            new[] { CrossplayService.DefaultBedrockPort + 1, bound });

        Assert.NotEqual(CrossplayService.DefaultBedrockPort, chosen);
        Assert.NotEqual(CrossplayService.DefaultBedrockPort + 1, chosen);
        Assert.NotEqual(bound, chosen);
    }

    [Fact]
    public void TheDefaultIsStillPreferredWhenNothingIsInTheWay()
    {
        // The search must not wander off the default for no reason: 19132 is the port every Bedrock
        // client tries first, and giving it up needlessly costs the user a number to type.
        Assert.Equal(CrossplayService.DefaultBedrockPort,
            new CrossplayService().PickBedrockPort(None, None));
    }

    [Fact]
    public void ASkippedPortIsExplained()
    {
        var said = new List<string>();
        new CrossplayService().PickBedrockPort(
            None,
            new[] { CrossplayService.DefaultBedrockPort },
            new Progress<string>(said.Add));

        // Progress<T> posts to the captured context; with none it runs on the thread pool, so the
        // report may not have landed yet. Either it says something or it says nothing — what must
        // never happen is a port silently moving with no line in the console to explain it.
        SpinWait.SpinUntil(() => said.Count > 0, TimeSpan.FromSeconds(2));
        Assert.Contains(said, m => m.Contains(CrossplayService.DefaultBedrockPort.ToString()));
    }

    // --- Telling "I checked" apart from "I could not look" ---

    [Fact]
    public void AReadOfTheUdpTableSaysThatItWasRead()
    {
        new PortService().FindFreeUdpPort(CrossplayService.DefaultBedrockPort,
            new HashSet<int>(), out var systemPortsRead);

        // The flag is the whole point: without it, "19132 is free" and "I have no idea what is
        // bound" were the same answer.
        Assert.True(systemPortsRead);
    }

    // --- Deleting a server's tunnels ---

    [Fact]
    public void TheBedrockTunnelGoesEvenWhenTheJavaPortCannotBeRead()
    {
        // server.properties can be unreadable or already gone. The Bedrock tunnel is identified by
        // Config.BedrockPort in servers.json and needs no file on disk, so losing one must not lose
        // the other — that is how orphan tunnels were being created.
        Assert.True(PlayitApiService.ShouldDeleteBedrockTunnel(userAskedToDelete: true, bedrockPort: 19133));
    }

    [Fact]
    public void NothingIsDeletedWhenTheUserDidNotAskOrThereIsNoPort()
    {
        Assert.False(PlayitApiService.ShouldDeleteBedrockTunnel(userAskedToDelete: false, bedrockPort: 19133));
        Assert.False(PlayitApiService.ShouldDeleteBedrockTunnel(userAskedToDelete: true, bedrockPort: null));
        Assert.False(PlayitApiService.ShouldDeleteBedrockTunnel(userAskedToDelete: true, bedrockPort: 0));
    }

    // --- The panel never being blank without a reason ---

    [Fact]
    public void EveryStateSaysSomething()
    {
        foreach (var state in Enum.GetValues<BedrockAddressState>())
        {
            var key = BedrockAddressStates.KeyFor(state);
            var text = Localizer.Get(key);

            // Localizer.Get returns the key itself when it is missing, and the key is built at run
            // time, so LocalizationTests — which only reads string literals — cannot see these four.
            Assert.NotEqual(key, text);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public void TheFourStatesAreDistinct()
    {
        var keys = Enum.GetValues<BedrockAddressState>()
            .Select(BedrockAddressStates.KeyFor)
            .ToList();

        // Two states sharing a line means one of the three situations that used to be an empty
        // panel is still unexplained.
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void ThePanelIsShownWhenCrossplayIsOnRatherThanWhenALookupSucceeded()
    {
        var block = BedrockPanelMarkup();

        // The bug in one line: the local port and the explanation used to hang off
        // HasBedrockAddress, which only a completed tunnel lookup can set.
        Assert.Contains("IsVisible=\"{Binding IsCrossplayOn}\"", block);
        Assert.Contains("{Binding BedrockLocalPortText}", block);
        Assert.Contains("{Binding BedrockStateText}", block);
    }

    [Fact]
    public void EveryNameTheBedrockPanelBindsToExistsOnTheViewModel()
    {
        // MainWindow.axaml is x:CompileBindings="False": a mistyped name raises nothing and the
        // panel simply stays empty, which is indistinguishable from the bug being unfixed.
        var names = Regex.Matches(BedrockPanelMarkup(), @"\{Binding ([A-Za-z0-9_]+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        var vm = typeof(ServerViewModel);
        foreach (var name in names)
            Assert.True(vm.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"ServerViewModel no tiene ninguna propiedad «{name}», que MainWindow.axaml enlaza");
    }

    /// <summary>The block of XAML that shows the local port and the state.</summary>
    private static string BedrockPanelMarkup()
    {
        var view = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml");
        var xaml = File.ReadAllText(view);

        var start = xaml.IndexOf("IsVisible=\"{Binding IsCrossplayOn}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "el panel local de Bedrock ya no está en MainWindow.axaml");

        // Back to the opening tag, forward to the end of that Grid.
        start = xaml.LastIndexOf("<Grid", start, StringComparison.Ordinal);
        var end = xaml.IndexOf("</Grid>", start, StringComparison.Ordinal);
        return xaml[start..(end + "</Grid>".Length)];
    }
}
