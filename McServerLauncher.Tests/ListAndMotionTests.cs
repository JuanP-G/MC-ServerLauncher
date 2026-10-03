using System.Text.RegularExpressions;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using static McServerLauncher.Services.PlayitApiService;

namespace McServerLauncher.Tests;

/// <summary>What a server card shows after the account is asked about its tunnels.</summary>
public class TunnelAddressSyncTests
{
    private static PlayitTunnel Tcp(int port, string host) => new("t" + port, "x", port, host, null, "tcp", 30000);
    private static PlayitTunnel Udp(int port, string host, int pub = 51917) => new("u" + port, "x", port, host, null, "udp", pub);

    [Fact]
    public void WhenTheAccountCannotBeAskedTheShownAddressStays() =>
        Assert.Equal("old.ply.gg", TunnelAddressSync.JavaAddress("old.ply.gg", tunnels: null, 25565));

    [Fact]
    public void WhenTheAccountSaysThereIsNoTunnelTheAddressGoes() =>
        Assert.Equal("", TunnelAddressSync.JavaAddress("old.ply.gg", [], 25565));

    [Fact]
    public void ABedrockTunnelOnTheJavaPortIsNotTheJavaAddress() =>
        Assert.Equal("", TunnelAddressSync.JavaAddress("old", [Udp(25565, "udp.ply.gg")], 25565));

    [Fact]
    public void TheJavaTunnelOnThePortIsShown() =>
        Assert.Equal("java.ply.gg", TunnelAddressSync.JavaAddress("old", [Tcp(25565, "java.ply.gg")], 25565));

    [Fact]
    public void AfterTheBedrockPortMovedTheOldAddressIsNotKept()
    {
        // The reported case: moved from 19132 to 19134, and the card went on showing 19132's tunnel,
        // which belonged to another server.
        var found = TunnelAddressSync.Bedrock([Udp(19132, "irvine-serenity.tun.ply.gg")], 19134);

        Assert.Equal(("", "", BedrockAddressState.LocalOnly), found);
    }

    [Fact]
    public void TheBedrockTunnelOnTheNewPortIsFound() =>
        Assert.Equal(("new.ply.gg", "40000", BedrockAddressState.Ready),
            TunnelAddressSync.Bedrock([Udp(19134, "new.ply.gg", 40000)], 19134));

    [Fact]
    public void ABedrockTunnelWithoutItsAddressYetIsWaiting() =>
        Assert.Equal(BedrockAddressState.Waiting, TunnelAddressSync.Bedrock([Udp(19134, "", 0)], 19134)!.Value.State);

    [Fact]
    public void WhenTheAccountCannotBeAskedThereIsNoBedrockAnswer() =>
        Assert.Null(TunnelAddressSync.Bedrock(null, 19134));
}

/// <summary>The server list's header and rows bind to names that exist.</summary>
public class ServerListBindingTests
{
    private static string MainWindow() =>
        File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MainWindow.axaml"));

    [Theory]
    [InlineData("ShowNewServerCommand")]
    [InlineData("EditServerCommand")]
    [InlineData("RemoveServerCommand")]
    [InlineData("ShowServerDetail")]
    [InlineData("ShowEmptyState")]
    [InlineData("NewServerPanel")]
    [InlineData("IsCreatingServer")]
    public void TheListAndTheDetailUseNamesTheMainViewModelHas(string name)
    {
        Assert.Contains(name, MainWindow());
        Assert.NotNull(typeof(MainViewModel).GetProperty(name));
    }

    [Fact]
    public void TheOldFooterButtonsAreGone()
    {
        var xaml = MainWindow();

        Assert.DoesNotContain("AddServerCommand", xaml);
        Assert.DoesNotContain("CreateServerCommand", xaml);
    }

    [Fact]
    public void TheRowActionsActOnTheirOwnRow()
    {
        var xaml = MainWindow();

        Assert.Matches(@"EditServerCommand[^>]*\s+CommandParameter=""\{Binding\}""", Regex.Replace(xaml, @"\s+", " "));
        Assert.Matches(@"RemoveServerCommand[^>]*\s+CommandParameter=""\{Binding\}""", Regex.Replace(xaml, @"\s+", " "));
    }
}

/// <summary>All the motion lives in one file.</summary>
public class MotionTests
{
    [Fact]
    public void NoViewDeclaresMotionOfItsOwn()
    {
        // One place to review it, and one place to tone it down.
        var root = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var offenders = Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => Path.GetFileName(f) != "Motion.axaml")
            .Where(f =>
            {
                var xaml = File.ReadAllText(f);
                return xaml.Contains("<Transitions>") || xaml.Contains("<Animation ") || xaml.Contains("<Animation>");
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheMotionStylesAreLoadedByTheApp() =>
        Assert.Contains("/Styles/Motion.axaml",
            File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "App.axaml")));
}
