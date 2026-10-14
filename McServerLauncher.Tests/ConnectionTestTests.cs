using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The connection test on the network page: what it waits for, and what it says.
/// </summary>
/// <remarks>
/// None of these reach the network. The ones that start a test hold it in the wait for the server,
/// before the first probe, and the rest call the pieces that judge and write directly.
/// </remarks>
[Collection("avalonia")]
public class ConnectionTestTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "mcl-nettest-" + Guid.NewGuid().ToString("N"));

    public ConnectionTestTests(AvaloniaFixture ui)
    {
        _ui = ui;
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "server.properties"), "server-port=25565\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private ServerViewModel NewServer() =>
        new(new ServerConfig { Name = "survival", FolderPath = _folder, Type = ServerType.Vanilla });

    /// <summary>
    /// "Start and test" on a server that is already getting ready waits for it, as it waits for one
    /// it starts itself.
    /// </summary>
    /// <remarks>
    /// While a start gets ready the state is still Stopped. The test used to give up on it after
    /// five seconds — or not wait at all, with Start greyed out — and report a server about to come
    /// up as off.
    /// </remarks>
    [Fact]
    public async Task AStartThatIsGettingReadyIsWaitedFor()
    {
        ConnectionTestViewModel? test = null;
        Task? run = null;
        _ui.Run(() =>
        {
            var server = NewServer();
            server.IsPreparing = true;
            test = server.ConnectionTest;
            run = test.StartServerAndRunCommand.ExecuteAsync(null);
        });

        await Task.Delay(700);
        string? title = null;
        var testing = false;
        _ui.Run(() =>
        {
            title = test!.LocalStep.Title;
            testing = test.IsTesting;
            test.Cancel();
        });
        await run!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(testing, "the test stopped waiting while the server was getting ready");
        Assert.Equal(Localizer.Get("Net_Starting"), title);
    }

    /// <summary>A test that stops goes back to each stretch's own name.</summary>
    /// <remarks>
    /// Resetting kept each step's title as it was, so a cancelled run — and the next one, until it
    /// reached each step — showed "Starting the server…" or the last verdict ("The tunnel is slow")
    /// beside "Waiting".
    /// </remarks>
    [Fact]
    public async Task AStoppedTestShowsEachStretchByItsName()
    {
        ConnectionTestViewModel? test = null;
        Task? run = null;
        _ui.Run(() =>
        {
            var server = NewServer();
            server.IsPreparing = true;
            test = server.ConnectionTest;
            test.TunnelStep.Set(StepState.Bad, Localizer.Get("Net_TunnelBad"), "", "180 ms");
            run = test.StartServerAndRunCommand.ExecuteAsync(null);
        });

        await Task.Delay(300);
        _ui.Run(() => test!.Cancel());
        await run!.WaitAsync(TimeSpan.FromSeconds(10));

        _ui.Run(() =>
        {
            Assert.Equal(Localizer.Get("Net_StepLocal"), test!.LocalStep.Title);
            Assert.Equal(Localizer.Get("Net_StepTunnel"), test.TunnelStep.Title);
            Assert.All(test.Steps, s => Assert.Equal(StepState.Waiting, s.State));
        });
    }
}
