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

    // The entry is only repaired when the copy it opens is gone, so what it opens must read back.

    [Theory]
    [InlineData(@"C:\Program Files\MC Server Launcher\McServerLauncher.exe")]
    [InlineData(@"D:\probar\prueba 6\McServerLauncher.exe")]
    public void TheWindowsTargetReadsBack(string path)
    {
        Assert.Equal(path, LoginStartupService.WindowsTarget(LoginStartupService.WindowsCommand(path)));
    }

    [Theory]
    [InlineData("/home/u/Applications/MC.AppImage")]
    [InlineData("/home/u/My Apps/Backup 100%/MC.AppImage")]
    [InlineData(@"/home/u/we""ird $dir`/x\y/MC.AppImage")]
    public void TheLinuxTargetReadsBack(string path)
    {
        Assert.Equal(path, LoginStartupService.LinuxTarget(LoginStartupService.LinuxEntry(path)));
    }

    [Theory]
    [InlineData("/Applications/MC Server Launcher.app")]
    [InlineData("/Users/a&b/McServerLauncher")]
    public void TheMacTargetReadsBack(string path)
    {
        Assert.Equal(path, LoginStartupService.MacTarget(LoginStartupService.MacAgent(path)));
    }

    [Theory]
    [InlineData("Hidden=true", true)]
    [InlineData("X-GNOME-Autostart-enabled=false", true)]
    [InlineData("X-GNOME-Autostart-enabled=true", false)]
    public void GnomesOffSwitchesAreRead(string line, bool off)
    {
        var entry = LoginStartupService.LinuxEntry("/x/MC.AppImage").Replace("X-GNOME-Autostart-enabled=true", line);
        Assert.Equal(off, LoginStartupService.LinuxEntryIsOff(entry));
    }
}
