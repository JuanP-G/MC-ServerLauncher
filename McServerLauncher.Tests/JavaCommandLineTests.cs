using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Java is started with a list of arguments, so what is in a file name can never change how many
/// there are.
/// </summary>
public class JavaCommandLineTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("-Xmx4G", new[] { "-Xmx4G" })]
    [InlineData("  -Xmx4G \t -Dfoo=bar\n-ea ", new[] { "-Xmx4G", "-Dfoo=bar", "-ea" })]
    [InlineData("-Dname=\"two words\" -ea", new[] { "-Dname=two words", "-ea" })]
    [InlineData("\"-Dname=two words\"", new[] { "-Dname=two words" })]
    [InlineData("-Dq=a\\\"b", new[] { "-Dq=a\"b" })]                         // an escaped quote is a quote
    [InlineData("-Dpath=C:\\mods\\x", new[] { "-Dpath=C:\\mods\\x" })]       // backslashes alone are literal
    [InlineData("\"C:\\two words\\\\\" x", new[] { "C:\\two words\\", "x" })] // \\ before the closing quote
    [InlineData("a \"\" b", new[] { "a", "", "b" })]                          // an empty argument is kept
    public void ALineIsCutTheWayTheOperatingSystemCutIt(string line, string[] expected)
    {
        Assert.Equal(expected, JavaCommandLine.Split(line));
    }

    [Fact]
    public void NullIsNoArguments() => Assert.Empty(JavaCommandLine.Split(null));

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("has \"quotes\" inside")]
    [InlineData("ends with backslash\\")]
    [InlineData("C:\\a path\\with\\slashes\\")]
    [InlineData("")]
    [InlineData("quote at end\"")]
    public void WhatIsShownIsWhatWouldBeReadBack(string arg)
    {
        string[] args = ["-jar", arg, "nogui"];

        Assert.Equal(args, JavaCommandLine.Split(JavaCommandLine.Display(args)));
    }

    [Fact]
    public void AJarNameWithAQuoteInItIsStillOneArgument()
    {
        var config = new ServerConfig { JarFile = "my \"special\" server.jar", MinRamGb = 1, MaxRamGb = 2 };

        var args = ServerProcessManager.BuildJavaArgumentList(config, javaMajor: 21);

        var jar = args.IndexOf("-jar");
        Assert.Equal("my \"special\" server.jar", args[jar + 1]);
        Assert.Equal("nogui", args[^1]);
        Assert.Equal(jar + 3, args.Count);   // -jar, the name, nogui: nothing split or merged
    }

    [Fact]
    public void ExtraArgumentsKeepTheirOwnGroupingAndComeBeforeTheJar()
    {
        var config = new ServerConfig
        {
            JarFile = "server.jar",
            MinRamGb = 1,
            MaxRamGb = 2,
            ExtraJvmArgs = "-Dmotd=\"hello there\" -XX:+UseZGC"
        };

        var args = ServerProcessManager.BuildJavaArgumentList(config, javaMajor: 21);

        Assert.Contains("-Dmotd=hello there", args);
        Assert.True(args.IndexOf("-XX:+UseZGC") < args.IndexOf("-jar"));
    }

    [Fact]
    public void TheConsoleLineQuotesWhatNeedsIt()
    {
        var config = new ServerConfig { JarFile = "my server.jar", MinRamGb = 1, MaxRamGb = 2 };

        var line = ServerProcessManager.BuildJavaArguments(config, javaMajor: 21);

        Assert.EndsWith("-jar \"my server.jar\" nogui", line);
    }
}
