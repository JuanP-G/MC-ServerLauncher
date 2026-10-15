using McServerLauncher.Localization;
using McServerLauncher.Models;
using System.Globalization;
using Avalonia.Controls;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using McServerLauncher.Views.ServerSettings;

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

    /// <summary>A server that is removed, or the app closing, stops its test.</summary>
    [Fact]
    public async Task ShuttingTheServerDownStopsItsTest()
    {
        ServerViewModel? server = null;
        ConnectionTestViewModel? test = null;
        Task? run = null, shutdown = null;
        _ui.Run(() =>
        {
            server = NewServer();
            server.IsPreparing = true;
            test = server.ConnectionTest;
            run = test.StartServerAndRunCommand.ExecuteAsync(null);
        });

        await Task.Delay(300);
        _ui.Run(() => shutdown = server!.ShutdownAsync());
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10));
        await run!.WaitAsync(TimeSpan.FromSeconds(10));

        _ui.Run(() => Assert.False(test!.IsTesting));
    }

    /// <summary>Leaving the network page stops the test that was running on it.</summary>
    [Fact]
    public async Task LeavingTheNetworkPageStopsTheTest()
    {
        ConnectionTestViewModel? test = null;
        Window? window = null;
        Task? run = null;
        _ui.Run(() =>
        {
            var server = NewServer();
            server.IsPreparing = true;
            test = server.ConnectionTest;
            var page = new NetworkSection(new ServerSettingsDraft(server.Config), test);
            AvaloniaFixture.WithoutIcons(page);
            // The steps' icons come from a template, after WithoutIcons has looked. Hidden, the card
            // is never measured, and what is tested is the page going away, not how it looks.
            page.FindControl<Control>("TestCard")!.IsVisible = false;
            window = new Window { Content = page };
            window.Show();
            AvaloniaFixture.Pump();
            run = test.StartServerAndRunCommand.ExecuteAsync(null);
        });

        await Task.Delay(300);
        _ui.Run(() =>
        {
            window!.Content = null;
            window.Hide();
        });
        await run!.WaitAsync(TimeSpan.FromSeconds(10));

        _ui.Run(() => Assert.False(test!.IsTesting));
    }

    /// <summary>A speed test that could not run says so, instead of blaming lag.</summary>
    /// <remarks>
    /// With a good round trip and nothing measured going up — a firewall cutting the upload, the
    /// speed host down — the step said the connection "has lag or loses packets".
    /// </remarks>
    [Fact]
    public void AnUnmeasuredLineIsNotBlamedOnLag()
    {
        _ui.Run(() =>
        {
            var test = NewServer().ConnectionTest;
            test.JudgeInternet(new LatencyStats([20, 21, 19, 22, 20, 21, 20, 19], 8), Throughput.None);

            Assert.Equal(StepState.Fair, test.InternetStep.State);
            Assert.Equal(Localizer.Get("Net_InternetNoSpeed"), test.InternetStep.Title);
            Assert.Equal(Localizer.Get("Net_InternetNoSpeedDetail"), test.InternetStep.Detail);
        });
    }

    /// <summary>The copied report is in the app's language, like the page it comes from.</summary>
    [Fact]
    public void TheReportSpeaksTheAppsLanguage()
    {
        _ui.Run(() =>
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
                var test = NewServer().ConnectionTest;
                test.LocalStep.Set(StepState.Good, Localizer.Get("Net_LocalGood"), "", "3 ms");
                test.InternetStep.Set(StepState.Fair, Localizer.Get("Net_InternetFair"), "", "40 ms");
                test.TunnelStep.Set(StepState.Skipped, Localizer.Get("Net_NoTunnel"), "");
                test.WriteReport([], Throughput.None, Throughput.None);

                Assert.Contains("[Bien]", test.Report);
                Assert.Contains("[Justo]", test.Report);
                Assert.Contains("[Sin probar]", test.Report);
                Assert.Contains("mín / media / máx", test.Report);
                Assert.DoesNotContain("[Good]", test.Report);
                Assert.DoesNotContain("peak", test.Report);
            }
            finally { CultureInfo.CurrentUICulture = original; }
        });
    }
}
