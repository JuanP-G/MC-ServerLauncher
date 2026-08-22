using McServerLauncher.Services;

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
}
