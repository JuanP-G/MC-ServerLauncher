using System.Text.RegularExpressions;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Every way of stopping a server gives it the same, generous time to save.
/// </summary>
/// <remarks>
/// Closing the app, updating it and deleting a server allowed fifteen seconds before killing the
/// process, while the Stop button allowed thirty. The commonest way of stopping a server was the one
/// most likely to cut it off halfway through writing its world, and large modpacks take longer than
/// fifteen seconds to save.
/// </remarks>
public class StopTimeoutTests
{
    [Fact]
    public void ThereIsTimeForALargeWorldToSave() =>
        Assert.True(ServerViewModel.StopTimeout >= TimeSpan.FromSeconds(60));

    [Fact]
    public void NoStopPicksItsOwnTimeout()
    {
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "ViewModels", "ServerViewModel.cs"));

        // Every call hands over the shared value; a literal here is how the two drifted apart.
        Assert.DoesNotMatch(new Regex(@"StopAsync\(\s*TimeSpan\."), source);
        Assert.Equal(3, Regex.Matches(source, @"_process\.StopAsync\(StopTimeout\)").Count);
    }
}
