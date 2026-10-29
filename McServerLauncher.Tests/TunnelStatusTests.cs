using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What the tunnel row and the signal dot say, as functions of the agent, the system service and the
/// server: pinned so a refactor of the view model cannot turn "no tunnel" into a green dot.
/// </summary>
public class TunnelStatusTests
{
    [Theory]
    [InlineData(AgentRunState.Running, PlayitState.Running)]
    [InlineData(AgentRunState.Downloading, PlayitState.Starting)]
    [InlineData(AgentRunState.Starting, PlayitState.Starting)]
    [InlineData(AgentRunState.Failed, PlayitState.Stopped)]
    [InlineData(AgentRunState.Stopped, PlayitState.Stopped)]
    [InlineData(AgentRunState.Unsupported, PlayitState.Stopped)]
    public void TheEmbeddedAgentDecidesWhenTheAccountIsConnected(AgentRunState agent, PlayitState expected) =>
        Assert.Equal(expected, PlayitStatus.Effective(agentHasSecret: true, agent, system: PlayitState.Running));

    [Theory]
    [InlineData(PlayitState.Running)]
    [InlineData(PlayitState.Starting)]
    [InlineData(PlayitState.Stopped)]
    public void WithoutAnAccountTheSystemServiceIsTheAnswer(PlayitState system) =>
        Assert.Equal(system, PlayitStatus.Effective(agentHasSecret: false, AgentRunState.Running, system));

    [Fact]
    public void WithAnAgentTheTextNeverMentionsTheSystemService()
    {
        var withInstalled = PlayitStatus.Text(true, AgentRunState.Stopped, PlayitState.Stopped, systemInstalled: true);
        var withoutInstalled = PlayitStatus.Text(true, AgentRunState.Stopped, PlayitState.Stopped, systemInstalled: false);

        Assert.Equal(withInstalled, withoutInstalled);
    }

    [Fact]
    public void WithoutAnAgentWhetherTheServiceIsInstalledChangesTheText()
    {
        var installed = PlayitStatus.Text(false, AgentRunState.Stopped, PlayitState.Stopped, systemInstalled: true);
        var missing = PlayitStatus.Text(false, AgentRunState.Stopped, PlayitState.Stopped, systemInstalled: false);

        Assert.NotEqual(installed, missing);
    }

    [Theory]
    [InlineData(ServerState.Stopped, false, PlayitState.Stopped, SignalKind.Off, false)]
    [InlineData(ServerState.Starting, true, PlayitState.Stopped, SignalKind.Transition, false)]
    [InlineData(ServerState.Stopping, true, PlayitState.Running, SignalKind.Transition, false)]
    [InlineData(ServerState.Running, false, PlayitState.Stopped, SignalKind.Accessible, false)]
    [InlineData(ServerState.Running, true, PlayitState.Running, SignalKind.Accessible, false)]
    [InlineData(ServerState.Running, true, PlayitState.Starting, SignalKind.NoTunnel, true)]
    [InlineData(ServerState.Running, true, PlayitState.Stopped, SignalKind.NoTunnel, true)]
    public void TheSignalIsGreenOnlyWhenPlayersCanReachTheServer(
        ServerState state, bool playitEnabled, PlayitState playit, SignalKind kind, bool warn)
    {
        var signal = ReachabilitySignal.For(state, playitEnabled, playit);

        Assert.Equal(kind, signal.Kind);
        Assert.Equal(warn, signal.ShowTunnelWarning);
    }
}
