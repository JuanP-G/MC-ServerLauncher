using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A start that is still getting ready cannot be started a second time.
/// </summary>
/// <remarks>
/// Before the process exists a start spends seconds — up to minutes, with a big world to back up —
/// checking the port, the path, the dependencies and the Java, and making the backup. All that time
/// the server still read as Stopped, so anything that starts a server without the Start button
/// (a second knock while waking on demand, an auto-restart) began a second start beside the first:
/// a second backup of the same world, and an error when the second one found the first one's process.
/// Here the second start comes while the first is zipping the world, which is where both used to
/// meet. There is no server.jar, so the start that gets through fails at the very end, once.
/// </remarks>
[Collection("avalonia")]
public class StartPreparationTests : IDisposable
{
    private readonly AvaloniaFixture _ui;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-prep-" + Guid.NewGuid().ToString("N"));

    public StartPreparationTests(AvaloniaFixture ui)
    {
        _ui = ui;
        var world = Path.Combine(_folder, "world");
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), new byte[256 * 1024]);
        File.WriteAllText(Path.Combine(_folder, "server.properties"), "server-port=1\n");
    }

    [Fact]
    public async Task ASecondStartWhileTheFirstPreparesDoesNothing()
    {
        var config = new ServerConfig { Name = "survival", FolderPath = _folder, Type = ServerType.Vanilla };

        ServerViewModel? server = null;
        Task? first = null, second = null;
        var couldStartMeanwhile = true;

        _ui.Run(() =>
        {
            server = new ServerViewModel(config);
            first = server.StartInternalAsync(isAutoRestart: true);
            couldStartMeanwhile = server.CanStart;
            second = server.StartInternalAsync(isAutoRestart: true);
        });

        await Task.WhenAll(first!, second!).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(couldStartMeanwhile, "Start stayed available while the first start was getting ready");
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "backups"), "*.zip"));

        var errors = 0;
        var canStartAfter = false;
        _ui.Run(() =>
        {
            errors = server!.ConsoleLines.Count(l => l.Text.StartsWith(ErrorPrefix, StringComparison.Ordinal));
            canStartAfter = server.CanStart;
        });
        Assert.Equal(1, errors);
        Assert.True(canStartAfter, "the server stayed unstartable after the start gave up");

        _ui.Run(() => server!.ShutdownAsync().GetAwaiter().GetResult());
    }

    /// <summary>What every error line starts with, in whichever language the tests run.</summary>
    private static string ErrorPrefix => Localizer.Get("Msg_ErrorFmt").Split("{0}")[0];

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
