using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The loader installer the pack's script downloads lands somewhere nobody else can reach.
/// </summary>
/// <remarks>
/// It went to <c>/tmp/mcsl-loader-installer.jar</c>: one fixed name in a folder every user of the
/// machine shares. Another user could plant a symlink there for curl to write through, or replace
/// the jar between the hash check and <c>java -jar</c>.
/// </remarks>
public class InstallScriptTempTests
{
    [Fact]
    public void UnixDownloadsIntoAFolderOfItsOwn()
    {
        var script = InstallScriptBuilder.Template("install-mods-unix.sh.in");

        Assert.Contains("mktemp -d", script);
        Assert.DoesNotContain("/mcsl-loader-installer.jar", script);
        // And takes the whole folder with it, on every way out of the install.
        Assert.DoesNotContain("rm -f \"$installer\"", script);
    }

    [Fact]
    public void WindowsDoesNotUseAFixedName()
    {
        var script = InstallScriptBuilder.Template("install-mods-windows.bat.in");

        Assert.Contains("%RANDOM%", script);
        Assert.DoesNotContain("mcsl-loader-installer.jar\"", script);
    }
}
