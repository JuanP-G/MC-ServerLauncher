using System.Net;
using System.Net.Sockets;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The Bedrock port of a Pumpkin server: where it is read from, how a new one is given a number
/// nobody else holds, and what a start does when somebody does.
/// </summary>
/// <remarks>
/// What these pin down came from a real afternoon. A sleeping Geyser server's wake-on-demand
/// listener held UDP 19132; a Pumpkin server asked for the same number, exited with code 1 and was
/// restarted three times — with nothing in the app that knew Pumpkin had a Bedrock port at all.
/// </remarks>
public sealed class NativeBedrockPortTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcsl-native-bedrock-" + Path.GetRandomFileName());
    private readonly List<string> _said = new();

    public NativeBedrockPortTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temporary folder */ }
    }

    private void WriteToml(string text) => File.WriteAllText(PumpkinConfig.PathIn(_folder), text);

    private static string Nethernet(int port) =>
        $"[networking.bedrock.nethernet]\nenabled = true\naddress = \"0.0.0.0:{port}\"\n";

    private ServerConfig Native(string name = "Pumpkin") =>
        new() { Name = name, FolderPath = _folder, Type = ServerType.Pumpkin };

    private StartPreflight Preflight(ServerConfig config, IReadOnlyDictionary<int, string>? owners = null) => new(
        config,
        new PreflightHost(
            Name: () => config.Name,
            Say: _said.Add,
            ConfigChanged: () => { },
            InstallMissing: (_, _) => Task.CompletedTask,
            BedrockPortOwners: () => owners),
        new JavaService(), new PortService(), new ServerPropertiesService());

    /// <summary>A port nothing holds for TCP or UDP right now.</summary>
    private static int FreePort()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var tcp = new TcpListener(IPAddress.Any, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException) { /* taken for UDP: try another */ }
        }
        throw new InvalidOperationException("No port free for both TCP and UDP.");
    }

    // --- reading the file ---

    [Fact]
    public void TheBedrockPortIsTheOneNetherNetBinds()
    {
        WriteToml(Nethernet(19140));

        Assert.Equal(19140, PumpkinConfig.BedrockPortIn(_folder));
    }

    [Theory]
    [InlineData("")]                                      // no file content at all
    [InlineData("[networking.java]\naddress = \"0.0.0.0:25570\"\n")]   // the table is not there yet
    [InlineData("[networking.bedrock.nethernet]\nenabled = true\n")]   // the table has no address
    public void AFileThatSaysNothingMeansPumpkinsDefault(string toml)
    {
        WriteToml(toml);

        // Pumpkin fills the file in with 19132 on its first start: the app has to count that number
        // before the file says it, or a server that has never run holds nothing.
        Assert.Equal(PumpkinConfig.DefaultBedrockPort, PumpkinConfig.BedrockPortIn(_folder));
    }

    [Fact]
    public void AFolderWithNoFileAtAllMeansPumpkinsDefaultToo() =>
        Assert.Equal(PumpkinConfig.DefaultBedrockPort, PumpkinConfig.BedrockPortIn(_folder));

    [Theory]
    [InlineData("[networking.bedrock]\nenabled = false\n")]
    [InlineData("[networking.bedrock.nethernet]\nenabled = false\naddress = \"0.0.0.0:19140\"\n")]
    public void BedrockSwitchedOffHoldsNoPort(string toml)
    {
        WriteToml(toml);

        Assert.Null(PumpkinConfig.BedrockPortIn(_folder));
    }

    // --- a new server's file ---

    [Fact]
    public void ANewServerWritesTheBedrockPortItWasGiven()
    {
        PumpkinConfig.WriteInitial(_folder, 25570, "x", seed: null, bedrockPort: 19150);

        Assert.Equal(19150, PumpkinConfig.BedrockPortIn(_folder));
        Assert.Equal("0.0.0.0:19150", PumpkinConfig.Read(PumpkinConfig.PathIn(_folder))["networking.bedrock.nethernet.address"]);
    }

    [Fact]
    public void ANewServerGivenNoBedrockPortLeavesPumpkinsDefault()
    {
        PumpkinConfig.WriteInitial(_folder, 25570, "x", seed: null);

        Assert.DoesNotContain("nethernet", File.ReadAllText(PumpkinConfig.PathIn(_folder)));
    }

    // --- one definition of "the port a server binds" ---

    [Fact]
    public void ANativeServerHoldsItsPumpkinPortAndAGeyserServerKeepsItsOwnRules()
    {
        WriteToml(Nethernet(19141));
        var pumpkin = Native();
        var geyser = new ServerConfig { Type = ServerType.Paper, CrossplayEnabled = true, BedrockPort = 19133 };

        Assert.Equal(19141, CrossplayService.BoundBedrockPort(pumpkin));
        Assert.Equal(19133, CrossplayService.BoundBedrockPort(geyser));

        // Not widened: "is Geyser set up?" still answers no for a server that has no Geyser, and the
        // settings page, the wake listener and the firewall notice all ask that.
        Assert.Null(CrossplayService.EffectiveBedrockPort(pumpkin));
    }

    [Fact]
    public void AGeyserServersPickSkipsThePortAPumpkinServerHolds()
    {
        WriteToml(Nethernet(CrossplayService.DefaultBedrockPort));
        var mine = new ServerConfig { Type = ServerType.Paper };

        var held = CrossplayService.PortsHeldBy(new[] { mine, Native() }, mine).ToArray();
        var picked = new CrossplayService().PickBedrockPort(held, Array.Empty<int>());

        Assert.Contains(CrossplayService.DefaultBedrockPort, held);
        Assert.NotEqual(CrossplayService.DefaultBedrockPort, picked);
    }

    [Fact]
    public void ANativeServerStoppedStillHoldsItsPort()
    {
        // Held by what the file says, not by what is bound: the app does not need Pumpkin to be
        // running to know that the number is spoken for.
        WriteToml(Nethernet(19142));

        var owners = CrossplayService.BedrockPortOwners(new[] { Native("Survival-RS") }, except: null);

        Assert.Equal("Survival-RS", owners[19142]);
    }

    [Fact]
    public void ANativeServerIsLeftOutOfItsOwnList()
    {
        WriteToml(Nethernet(19143));
        var mine = Native();

        Assert.Empty(CrossplayService.PortsHeldBy(new[] { mine }, mine));
    }

    [Fact]
    public void ANewNativeServersPickAvoidsWhatIsHeld()
    {
        var picked = new CrossplayService().PickNativeBedrockPort(new[] { 19132, 19133, 19134 });

        Assert.DoesNotContain(picked, new[] { 19132, 19133, 19134 });
        Assert.False(new PortService().IsPortInUse(picked));
        Assert.False(new PortService().IsUdpPortInUse(picked));
    }

    // --- starting ---

    [Fact]
    public void AJavaServerHasNoNativeBedrockPortToCheck()
    {
        var java = new ServerConfig { Name = "Java", FolderPath = _folder, Type = ServerType.Paper };

        Assert.True(Preflight(java, new Dictionary<int, string> { [19132] = "Other" }).BedrockPortIsFree());
        Assert.Empty(_said);
    }

    [Fact]
    public void ANativeServerOnAFreePortStarts()
    {
        WriteToml(Nethernet(FreePort()));

        Assert.True(Preflight(Native()).BedrockPortIsFree());
        Assert.Empty(_said);
    }

    [Fact]
    public void ANativeServerWithBedrockOffStartsWhateverIsOnTheDefaultPort()
    {
        WriteToml("[networking.bedrock]\nenabled = false\n");

        Assert.True(Preflight(Native(), new Dictionary<int, string> { [19132] = "Other" }).BedrockPortIsFree());
    }

    [Fact]
    public void AStartIsRefusedWhenAnotherServerHoldsThePortAndSaysWhichOne()
    {
        // The case that started this: the other server is asleep, so nothing is bound as far as the
        // system's tables can tell, and Pumpkin found the port taken anyway.
        var port = FreePort();
        WriteToml(Nethernet(port));

        var ready = Preflight(Native(), new Dictionary<int, string> { [port] = "Survival" }).BedrockPortIsFree();

        Assert.False(ready);
        var line = Assert.Single(_said);
        Assert.Contains("Survival", line);
        Assert.Contains(port.ToString(), line);
        Assert.Contains("pumpkin.toml", line);
    }

    [Fact]
    public void TheSuggestedPortInTheMessageIsOneThatIsFree()
    {
        // Held at the very number a search starts from, so a suggestion that ignored the owners
        // would name it again.
        var held = CrossplayService.DefaultBedrockPort;
        WriteToml(Nethernet(held));
        var owners = new Dictionary<int, string> { [held] = "Survival", [held + 1] = "Creative" };

        Preflight(Native(), owners).BedrockPortIsFree();

        // The last number in the line is the suggestion.
        var numbers = System.Text.RegularExpressions.Regex.Matches(_said.Single(), @"\d{4,5}")
            .Select(m => int.Parse(m.Value)).ToArray();
        var suggested = numbers[^1];
        Assert.DoesNotContain(suggested, owners.Keys);
        Assert.False(new PortService().IsPortInUse(suggested));
        Assert.False(new PortService().IsUdpPortInUse(suggested));
    }

    [Fact]
    public void AStartIsRefusedWhenAProgramHoldsThePortOverUdp()
    {
        using var holder = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)holder.Client.LocalEndPoint!).Port;
        WriteToml(Nethernet(port));

        Assert.False(Preflight(Native()).BedrockPortIsFree());
        Assert.Single(_said);
    }

    [Fact]
    public void AStartIsRefusedWhenAProgramHoldsThePortOverTcp()
    {
        // Pumpkin opens the number for TCP as well (the signalling), so a TCP holder stops it too.
        var holder = new TcpListener(IPAddress.Any, 0);
        holder.Start();
        try
        {
            WriteToml(Nethernet(((IPEndPoint)holder.LocalEndpoint).Port));

            Assert.False(Preflight(Native()).BedrockPortIsFree());
            Assert.Single(_said);
        }
        finally { holder.Stop(); }
    }
}
