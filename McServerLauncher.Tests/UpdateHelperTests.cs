using System.Diagnostics;
using System.Security.Cryptography;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The second copy of the app that runs the Windows installer: it runs exactly the file that was
/// verified, and puts the app back when it cannot.
/// </summary>
public class UpdateHelperTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mcl-helper-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    /// <summary>A package in a folder named the way the app names them, under this test's temp root.</summary>
    private string Package(string content = "installer bytes")
    {
        var folder = Path.Combine(_temp, SelfUpdater.PackageFolderPrefix + "abcd1234.xyz");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Setup.exe");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private int Apply(string installer, string sha, Func<string, int> run, out int relaunched,
        int parentPid = int.MaxValue, TimeSpan? wait = null)
    {
        var count = 0;
        var code = UpdateHelper.Apply(parentPid, installer, sha, run, () => count++, _log.Add,
            wait ?? TimeSpan.FromMilliseconds(200), _temp);
        relaunched = count;
        return code;
    }

    [Fact]
    public void TheVerifiedFileIsRunAndItsExitCodeIsReturned()
    {
        var path = Package();
        var ran = new List<string>();

        var code = Apply(path, Sha(path), p => { ran.Add(p); return 0; }, out var relaunched);

        Assert.Equal(0, code);
        Assert.Equal([path], ran);
        Assert.Equal(0, relaunched);
    }

    [Fact]
    public void TheHashIsComparedWithoutRegardToCase()
    {
        var path = Package();

        var code = Apply(path, Sha(path).ToLowerInvariant(), _ => 0, out _);

        Assert.Equal(0, code);
    }

    [Fact]
    public void AFileThatIsNotTheVerifiedOneIsNeverRunAndTheAppComesBack()
    {
        var path = Package();
        var verified = Sha(path);
        File.WriteAllText(path, "somebody swapped it");   // after it was verified, before it ran
        var ran = false;

        var code = Apply(path, verified, _ => { ran = true; return 0; }, out var relaunched);

        Assert.Equal(UpdateHelper.NotTheVerifiedFile, code);
        Assert.False(ran);
        Assert.Equal(1, relaunched);
        Assert.Contains(_log, l => l.Contains("not the file that was verified"));
    }

    [Fact]
    public void AMissingFileBringsTheAppBackToo()
    {
        var path = Package();
        var verified = Sha(path);
        File.Delete(path);

        var code = Apply(path, verified, _ => 0, out var relaunched);

        Assert.Equal(UpdateHelper.Failed, code);
        Assert.Equal(1, relaunched);
    }

    [Fact]
    public void AnInstallerThatCannotStartBringsTheAppBack()
    {
        // Declining the elevation prompt looks like this: starting the process throws.
        var path = Package();

        var code = Apply(path, Sha(path), _ => throw new InvalidOperationException("declined"), out var relaunched);

        Assert.Equal(UpdateHelper.Failed, code);
        Assert.Equal(1, relaunched);
    }

    [Fact]
    public void AnInstallerThatFailsBringsTheAppBackAndReportsItsCode()
    {
        var path = Package();

        var code = Apply(path, Sha(path), _ => 5, out var relaunched);

        Assert.Equal(5, code);
        Assert.Equal(1, relaunched);
    }

    [Fact]
    public void ASuccessfulInstallDoesNotRelaunchBecauseTheInstallerDoesIt()
    {
        var path = Package();

        Apply(path, Sha(path), _ => 0, out var relaunched);

        Assert.Equal(0, relaunched);
    }

    [Fact]
    public void WhileTheInstallerRunsNobodyCanWriteToRenameOrDeleteIt()
    {
        if (!OperatingSystem.IsWindows()) return;   // sharing modes are only enforced by Windows

        var path = Package();
        var blocked = new List<string>();

        Apply(path, Sha(path), p =>
        {
            try { using var _ = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { blocked.Add("write"); }
            try { File.Move(p, p + ".moved"); }
            catch (IOException) { blocked.Add("rename"); }
            try { File.Delete(p); }
            catch (IOException) { blocked.Add("delete"); }
            // Reading stays possible: the installer itself, and Windows loading it, have to.
            using var read = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return 0;
        }, out _);

        Assert.Equal(["write", "rename", "delete"], blocked);
    }

    [Fact]
    public void ARunningParentIsWaitedForAndThenTheInstallerIsNotRun()
    {
        var path = Package();
        var ran = false;

        // This very process is "the app" and is not exiting.
        var code = Apply(path, Sha(path), _ => { ran = true; return 0; }, out var relaunched,
            parentPid: Environment.ProcessId, wait: TimeSpan.FromMilliseconds(150));

        Assert.Equal(UpdateHelper.ParentStillRunning, code);
        Assert.False(ran);
        Assert.Equal(0, relaunched);   // the app is still up: nothing to bring back
    }

    [Fact]
    public void AParentThatExitsMeanwhileLetsTheInstallerRun()
    {
        var path = Package();
        using var parent = StartShortLivedProcess();

        var code = Apply(path, Sha(path), _ => 0, out _, parent.Id, TimeSpan.FromSeconds(20));

        Assert.Equal(0, code);
        Assert.True(parent.HasExited);
    }

    // ---- what may be run at all ----

    [Fact]
    public void OnlyAFileInOneOfTheAppsDownloadFoldersIsAcceptable()
    {
        var good = Package();
        Assert.True(UpdateHelper.IsPackagePath(good, _temp));

        // Not in an "mcsl-" folder, not directly under the temp root, a different root, a traversal.
        var elsewhere = Path.Combine(_temp, "other", "Setup.exe");
        var tooDeep = Path.Combine(_temp, SelfUpdater.PackageFolderPrefix + "abcd1234.xyz", "sub", "Setup.exe");
        var sneaky = Path.Combine(_temp, SelfUpdater.PackageFolderPrefix + "abcd1234.xyz", "..", "..", "Setup.exe");

        Assert.False(UpdateHelper.IsPackagePath(elsewhere, _temp));
        Assert.False(UpdateHelper.IsPackagePath(tooDeep, _temp));
        Assert.False(UpdateHelper.IsPackagePath(sneaky, _temp));
        Assert.False(UpdateHelper.IsPackagePath(good, Path.Combine(_temp, "elsewhere")));
        Assert.False(UpdateHelper.IsPackagePath("", _temp));
    }

    [Fact]
    public void AFileOutsideThoseFoldersIsRefusedBeforeAnythingIsRead()
    {
        var path = Package();
        var outside = Path.Combine(_temp, "Setup.exe");
        File.Copy(path, outside);
        var ran = false;

        var code = Apply(outside, Sha(outside), _ => { ran = true; return 0; }, out var relaunched);

        Assert.Equal(UpdateHelper.NotAnUpdatePackage, code);
        Assert.False(ran);
        Assert.Equal(0, relaunched);
    }

    [Fact]
    public void OnlyTheSwitchFirstMakesAStartTheHelpers()
    {
        Assert.True(UpdateHelper.IsHelper([UpdateHelper.Switch, "1", "x", "y"]));
        Assert.False(UpdateHelper.IsHelper([]));
        Assert.False(UpdateHelper.IsHelper(["--other", UpdateHelper.Switch]));
    }

    [Fact]
    public void BadArgumentsEndTheHelperWithoutRunningAnything()
    {
        Assert.Equal(UpdateHelper.Failed, UpdateHelper.Run([UpdateHelper.Switch]));
        Assert.Equal(UpdateHelper.Failed, UpdateHelper.Run([UpdateHelper.Switch, "not-a-pid", "x", "y"]));
    }

    private static Process StartShortLivedProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 0.3\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return Process.Start(psi)!;
    }

    public UpdateHelperTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { /* temp */ }
    }
}
