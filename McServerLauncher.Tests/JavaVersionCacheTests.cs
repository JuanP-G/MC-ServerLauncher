using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A Java is asked for its version once, not every time a server starts.
/// </summary>
/// <remarks>
/// Asking starts a JVM. It used to happen twice on every start — to check the Java and again to
/// build the command line — on the UI thread, which is the freeze between pressing Start and the
/// button reacting. The answer only changes when the executable does.
/// </remarks>
public class JavaVersionCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-java-" + Guid.NewGuid().ToString("N"));
    private string Calls => Path.Combine(_dir, "calls.txt");

    /// <summary>A "java" that says it is 21 and leaves a line behind every time it runs.</summary>
    private string FakeJava()
    {
        Directory.CreateDirectory(_dir);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "java.cmd");
            File.WriteAllLines(cmd, new[]
            {
                "@echo off",
                "echo ran>> \"" + Calls + "\"",
                "echo openjdk version \"21.0.2\" 2024-01-16 1>&2"
            });
            return cmd;
        }

        var sh = Path.Combine(_dir, "java");
        File.WriteAllLines(sh, new[]
        {
            "#!/bin/sh",
            "echo ran >> '" + Calls + "'",
            "echo 'openjdk version \"21.0.2\" 2024-01-16' 1>&2"
        });
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private int TimesRun() => File.Exists(Calls) ? File.ReadAllLines(Calls).Length : 0;

    [Fact]
    public void TheSameJavaIsAskedOnce()
    {
        var java = FakeJava();
        var service = new JavaService();

        Assert.Equal(21, service.GetMajorVersion(java));
        Assert.Equal(21, service.GetMajorVersion(java));
        Assert.Equal(21, new JavaService().GetMajorVersion(java));   // remembered for the app, not per instance

        Assert.Equal(1, TimesRun());
    }

    [Fact]
    public void AJavaReplacedInPlaceIsAskedAgain()
    {
        var java = FakeJava();
        var service = new JavaService();
        Assert.Equal(21, service.GetMajorVersion(java));

        File.AppendAllText(java, Environment.NewLine);   // an update changes the file
        Assert.Equal(21, service.GetMajorVersion(java));

        Assert.Equal(2, TimesRun());
    }

    [Fact]
    public void SomethingThatIsNotJavaIsZeroAndNotRemembered() =>
        Assert.Equal(0, new JavaService().GetMajorVersion(Path.Combine(_dir, "no-such-java")));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
