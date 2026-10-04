using System.Diagnostics;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A short external command runs within its deadline, or is stopped at it.
/// </summary>
/// <remarks>
/// The helpers this replaced read the output to its end before waiting, which waits for the process
/// to finish — so their five-second limit never applied to a command that hung.
/// </remarks>
public class ProcessRunnerTests
{
    private static (string File, string[] Args) Shell(string windows, string unix) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", windows })
            : ("/bin/sh", new[] { "-c", unix });

    [Fact]
    public void WhatItPrintsIsCollected()
    {
        var (file, args) = Shell("echo hola", "echo hola");

        var r = ProcessRunner.Run(file, args, TimeSpan.FromSeconds(10));

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hola", r.Output);
    }

    [Fact]
    public void ACommandThatHangsIsStoppedAtTheDeadline()
    {
        var (file, args) = Shell("ping -n 30 127.0.0.1 >nul", "sleep 30");
        var clock = Stopwatch.StartNew();

        var r = ProcessRunner.Run(file, args, TimeSpan.FromSeconds(1));

        Assert.Equal(-1, r.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void SomethingThatCannotRunIsMinusOneAndDoesNotThrow() =>
        Assert.Equal(-1, ProcessRunner.Run("no-such-command-mcsl", Array.Empty<string>(), TimeSpan.FromSeconds(2)).ExitCode);
}
