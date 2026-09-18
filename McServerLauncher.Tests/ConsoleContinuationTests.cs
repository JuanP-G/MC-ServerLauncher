using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Lines that belong to the entry above them, and the JVM's own warnings.
/// </summary>
/// <remarks>
/// <para>
/// From a real start of a Fabric server with 76 mods: the whole list of mods came out red, as though
/// every one of them had crashed. The loader indents that list with tabs, and a tab was taken to mean
/// a stack trace. Meanwhile the lines under a real warning — Distant Horizons's five-line one, the
/// ones under "Warnings were found!" — came out grey, because each line was judged on its own and
/// only the first carried a level.
/// </para>
/// <para>
/// Every line here is copied from that log.
/// </para>
/// </remarks>
public class ConsoleContinuationTests
{
    /// <summary>Runs lines through the classifier the way the console does, one after another.</summary>
    private static List<(string Line, ConsoleLineKind Kind)> Replay(params string[] lines)
    {
        var result = new List<(string, ConsoleLineKind)>();
        ConsoleLineKind? previous = null;

        foreach (var line in lines)
        {
            var kind = ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout, previous);
            result.Add((line, kind));
            previous = kind;
        }
        return result;
    }

    [Fact]
    public void FabricsListOfModsIsNotAListOfErrors()
    {
        var run = Replay(
            "[21:05:51] [main/INFO]: Loading 76 mods:",
            "\t- appleskin 3.0.10+mc26.2",
            "\t- balm 26.2.0.8",
            "\t   \\-- kuma_api 26.2.0.1",
            "\t- bluemap 5.27",
            "\t   |-- com_flowpowered_flow-math 1.0.3",
            "\t        \\-- com_electronwill_night-config_toml 3.9.0",
            "\t- fabricloader 0.19.5",
            "\t   \\-- mixinextras 0.5.5",
            "[21:05:51] [main/INFO]: SpongePowered MIXIN Subsystem Version=0.8.7");

        Assert.All(run, entry => Assert.Equal(ConsoleLineKind.Info, entry.Kind));
    }

    [Fact]
    public void TheLinesUnderAWarningAreTheWarning()
    {
        var run = Replay(
            "[21:05:51] [main/WARN]: Warnings were found!",
            " - ¡El mod 'Forge Config API Port' (forgeconfigapiport) 26.2.1 recomienda cualquier versión de modmenu, que no tienes!",
            "\t - Debería instalar cualquier versión de modmenu para una experiencia óptima.",
            "[21:05:51] [main/INFO]: Loading 76 mods:");

        Assert.Equal(ConsoleLineKind.Warn, run[1].Kind);
        Assert.Equal(ConsoleLineKind.Warn, run[2].Kind);
        Assert.Equal(ConsoleLineKind.Info, run[3].Kind);   // its own entry again
    }

    [Fact]
    public void AMultiLineWarningStaysYellowToTheEnd()
    {
        // Distant Horizons's, word for word. Only the first line carries a level; the four that
        // explain it used to come out as ordinary grey output.
        var run = Replay(
            "[21:06:01] [Server thread/WARN]: Distant Horizons: G1 Garbage collector detected.",
            "This can cause FPS stuttering. ",
            "It's recommended to use a concurrent garbage collector ",
            "like ZGC (Java 21+) or Shenandoah (Java 8 through 17) ",
            "for a smoother experience. ",
            "This warning can be disabled in the DH config.",
            "[21:06:01] [Server thread/INFO]: Explicit Garbage Collection: [Enabled]");

        Assert.All(run.Take(6), entry => Assert.Equal(ConsoleLineKind.Warn, entry.Kind));
        Assert.Equal(ConsoleLineKind.Info, run[6].Kind);
    }

    [Fact]
    public void AnExceptionsMessageBelongsToTheErrorAboveIt()
    {
        // Better than before, not just no worse: the line that names the exception carries no level
        // and used to be painted as ordinary output, right under the red line announcing it.
        var run = Replay(
            "[12:00:00] [Server thread/ERROR]: Encountered an unexpected exception",
            "java.lang.IllegalStateException: Failed to load chunk",
            "\tat net.minecraft.server.MinecraftServer.run(MinecraftServer.java:100)",
            "Caused by: java.io.IOException: disk full",
            "\t... 12 more");

        Assert.All(run, entry => Assert.Equal(ConsoleLineKind.Error, entry.Kind));
    }

    [Fact]
    public void AStackFrameIsAnErrorEvenWithNothingAboveIt()
    {
        // printStackTrace to standard output, with no log line announcing it: the frames alone still
        // have to stand out, and they are recognised by shape, not by indentation.
        Assert.Equal(ConsoleLineKind.Error, ConsoleLineClassifier.Classify(
            "\tat java.base/java.lang.Thread.run(Thread.java:840)", ConsoleSource.Stdout, ConsoleLineKind.Info));
    }

    [Fact]
    public void WithNothingAboveItAPrefixlessLineIsPlainOutput()
    {
        Assert.Equal(ConsoleLineKind.Info, ConsoleLineClassifier.Classify(
            "Starting net.fabricmc.loader.impl.game.minecraft.BundlerClassPathCapture", ConsoleSource.Stdout));
    }

    [Fact]
    public void ALineWithItsOwnPrefixIsNeverAContinuation()
    {
        // Even one without a level in the brackets — it opens an entry of its own.
        Assert.Equal(ConsoleLineKind.Info, ConsoleLineClassifier.Classify(
            "[12:34:56] [Render thread]: replaying the log", ConsoleSource.Stdout, ConsoleLineKind.Error));
    }

    // --- Standard error ---

    [Theory]
    [InlineData("WARNING: A restricted method in java.lang.System has been called")]
    [InlineData("WARNING: Use --enable-native-access=ALL-UNNAMED to avoid a warning for callers in this module")]
    [InlineData("WARNING: sun.misc.Unsafe::objectFieldOffset will be removed in a future release")]
    public void TheJvmsOwnWarningsAreWarnings(string line)
    {
        // Printed to standard error, in a format the JVM fixes. Red made a healthy start look like a
        // failing one.
        Assert.Equal(ConsoleLineKind.Warn, ConsoleLineClassifier.Classify(line, ConsoleSource.Stderr));
    }

    [Fact]
    public void ABlankLineIsNeverAnError()
    {
        Assert.Equal(ConsoleLineKind.Info, ConsoleLineClassifier.Classify("", ConsoleSource.Stderr));
    }
}
