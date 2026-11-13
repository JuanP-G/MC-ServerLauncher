using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Opening the app at sign-in. What reaches the system is checked here, because a wrong entry fails
/// silently: the next sign-in simply opens nothing.
/// </summary>
public class LoginStartupTests
{
    [Fact]
    public void TheWindowsCommandQuotesThePathAndSaysItIsASignIn()
    {
        Assert.Equal("\"C:\\Program Files\\MC Server Launcher\\McServerLauncher.exe\" --login",
            LoginStartupService.WindowsCommand(@"C:\Program Files\MC Server Launcher\McServerLauncher.exe"));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void TaskManagersOffMarkIsRead(byte[] state, bool disabled)
    {
        Assert.Equal(disabled, LoginStartupService.IsDisabledMark(state));
    }

    [Fact]
    public void TheLinuxEntryRunsThisCopyWithTheSignInArgument()
    {
        var entry = LoginStartupService.LinuxEntry("/home/u/My Apps/MC.AppImage");
        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Exec=\"/home/u/My Apps/MC.AppImage\" --login", entry);
        Assert.Contains("Type=Application", entry);
    }

    [Fact]
    public void AMacBundleIsOpenedWithOpen()
    {
        var plist = LoginStartupService.MacAgent("/Applications/MC Server Launcher.app");
        Assert.Contains("<string>/usr/bin/open</string>", plist);
        Assert.Contains("<string>/Applications/MC Server Launcher.app</string>", plist);
        Assert.Contains("<string>--login</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.StartsWith("<?xml", plist);
    }

    [Fact]
    public void AMacPathIsEscapedForThePlist()
    {
        var plist = LoginStartupService.MacAgent("/Users/a&b/McServerLauncher");
        Assert.Contains("<string>/Users/a&amp;b/McServerLauncher</string>", plist);
    }

    [Theory]
    [InlineData(new[] { "--login" }, true)]
    [InlineData(new[] { "--LOGIN" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--update-helper" }, false)]
    public void ALaunchAtSignInIsRecognised(string[] args, bool login)
    {
        Assert.Equal(login, LoginStartupService.IsLoginLaunch(args));
    }
}
