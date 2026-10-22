using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Which extra JVM arguments take the memory decision away from the app, and which do not.
/// </summary>
/// <remarks>
/// Getting this wrong either way hurts somebody: too eager, and every imported server with a
/// tutorial's flags stays on fixed memory; too lax, and the app lays its own collector and margins
/// under numbers the owner chose on purpose.
/// </remarks>
public class JvmArgsTests
{
    [Theory]
    [InlineData("-Xmx6G")]
    [InlineData("-Xms2048M")]
    [InlineData("-xmx4g")]
    [InlineData("-Xmn512M")]
    [InlineData("-XX:MaxRAMPercentage=75")]
    [InlineData("-XX:MaxRAM=8g")]
    [InlineData("-XX:MaxHeapSize=4294967296")]
    [InlineData("-XX:+UseZGC")]
    [InlineData("-XX:+UseShenandoahGC")]
    [InlineData("-XX:+UseParallelGC")]
    [InlineData("-XX:+UseSerialGC")]
    [InlineData("-XX:MaxHeapFreeRatio=50")]
    [InlineData("-XX:G1PeriodicGCInterval=0")]
    [InlineData("-XX:SoftMaxHeapSize=2g")]
    [InlineData("-XX:+AlwaysPreTouch")]
    [InlineData("-Xgcpolicy:gencon")]
    [InlineData("-Xsoftmx2g")]
    public void ArgumentsThatSizeOrReturnMemoryMakeTheServerManual(string flag)
    {
        Assert.True(JvmArgs.SetsMemory("-Dfile.encoding=UTF-8 " + flag));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-XX:+UseG1GC")]
    [InlineData("-Dfile.encoding=UTF-8 --add-opens java.base/java.lang=ALL-UNNAMED")]
    [InlineData("-XX:+UseCompactObjectHeaders")]
    [InlineData("-XX:MaxGCPauseMillis=200")]
    [InlineData("-Dlog4j2.formatMsgNoLookups=true")]
    public void OtherArgumentsLeaveTheMemoryToTheApp(string? args)
    {
        Assert.False(JvmArgs.SetsMemory(args));
    }

    [Fact]
    public void AikarsFlagsAreAChoiceOfTheirOwn()
    {
        // They fix the heap at its ceiling and touch all of it at start: the opposite of the profile.
        const string aikar = "-Xms10G -Xmx10G -XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 " +
                             "-XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch " +
                             "-XX:G1NewSizePercent=30 -XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M";

        Assert.True(JvmArgs.SetsMemory(aikar));
    }

    [Fact]
    public void AQuotedValueStaysOneArgument()
    {
        Assert.Equal(new[] { "-Dname=Mi servidor", "-Xmx4G" }, JvmArgs.Split("\"-Dname=Mi servidor\"  -Xmx4G"));
    }

    [Fact]
    public void ADisabledClassPointerCountsAsSayingSomethingAboutHeaders()
    {
        // Compact headers need compressed class pointers; a JVM given both refuses to start.
        Assert.True(JvmArgs.MentionsCompactHeaders("-XX:-UseCompressedClassPointers"));
        Assert.True(JvmArgs.MentionsCompactHeaders("-XX:-UseCompactObjectHeaders"));
        Assert.False(JvmArgs.MentionsCompactHeaders("-XX:+UseG1GC"));
    }
}
