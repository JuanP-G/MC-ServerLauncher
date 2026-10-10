using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Restarting the app starts a new copy on every platform, macOS included.
/// </summary>
/// <remarks>
/// On macOS the app relaunches its .app bundle. Opening a bundle the plain way, while this copy is
/// still running, only brings this copy to the front — and it is about to exit, so changing the
/// language closed the app for good. <c>open -n</c> asks for a new copy regardless.
/// </remarks>
public class RelaunchTests
{
    [Fact]
    public void ABundleOnMacOsOpensANewCopy()
    {
        var start = MainViewModel.RelaunchStartInfo("/Applications/MC Server Launcher.app", macOS: true);

        Assert.Equal("open", start.FileName);
        Assert.Equal(new[] { "-n", "/Applications/MC Server Launcher.app" }, start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Theory]
    [InlineData(@"C:\Program Files\MC Server Launcher\McServerLauncher.exe", false)]
    [InlineData("/home/ana/Apps/MC-ServerLauncher.AppImage", false)]
    [InlineData("/Applications/MC Server Launcher.app/Contents/MacOS/McServerLauncher", true)]
    public void AnythingElseIsStartedAsItIs(string target, bool macOS)
    {
        var start = MainViewModel.RelaunchStartInfo(target, macOS);

        Assert.Equal(target, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Empty(start.ArgumentList);
    }
}
