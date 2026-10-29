using System.Net;
using System.Net.Sockets;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The checks that come before a server's JVM is launched, run without a window: what lets a start
/// through and what stops it, in the unattended case where nobody can answer a question.
/// </summary>
public class StartPreflightTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-preflight-" + Path.GetRandomFileName());
    private readonly List<string> _said = new();
    private int _configChanges;

    public StartPreflightTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    private StartPreflight Preflight(ServerConfig config) => new(config,
        new PreflightHost(
            Name: () => "Test",
            Say: _said.Add,
            ConfigChanged: () => _configChanges++,
            InstallMissing: (_, _) => Task.CompletedTask),
        new JavaService(), new PortService(), new ServerPropertiesService());

    private ServerConfig Config(ServerType type = ServerType.Vanilla) =>
        new() { Name = "Test", FolderPath = _dir, Type = type };

    private void WritePort(int port) =>
        File.WriteAllText(Path.Combine(_dir, "server.properties"), $"server-port={port}\n");

    [Fact]
    public async Task APortNothingListensOnLetsTheStartThrough()
    {
        // Found by asking the OS for a free one, then letting it go.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        WritePort(port);

        Assert.True(await Preflight(Config()).PortIsFreeAsync(isAutoRestart: false));
        Assert.Empty(_said);
    }

    [Fact]
    public async Task ABusyPortGivesUpAnUnattendedStartWithoutAskingAnything()
    {
        var holder = new TcpListener(IPAddress.Any, 0);
        holder.Start();
        try
        {
            WritePort(((IPEndPoint)holder.LocalEndpoint).Port);

            Assert.False(await Preflight(Config()).PortIsFreeAsync(isAutoRestart: true));
            Assert.Single(_said);   // it said why, and no dialog was shown (the test would hang)
        }
        finally { holder.Stop(); }
    }

    [Fact]
    public async Task AFolderWithNoServerPropertiesHasNoPortToCheck()
    {
        Assert.True(await Preflight(Config()).PortIsFreeAsync(isAutoRestart: true));
    }

    [Fact]
    public async Task APathThatTheServerAcceptsIsNotTouched()
    {
        Assert.True(await Preflight(Config(ServerType.Vanilla)).TryFixRejectedPathAsync(isAutoRestart: false));
        Assert.Equal(0, _configChanges);
        Assert.Empty(_said);
    }

    [Fact]
    public async Task AServerWithNoModsHasNothingMissing()
    {
        Assert.True(await Preflight(Config()).CheckContentDependenciesAsync(isAutoRestart: true));
        Assert.Empty(_said);
    }
}
