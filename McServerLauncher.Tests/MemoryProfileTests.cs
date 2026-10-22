using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The memory a server starts with: the mode in effect, the ceiling, and the flags for each Java.
/// </summary>
/// <remarks>
/// The flags are the ones measured on 2026-10-21/22 with bots on Minecraft 26.2: an empty server
/// came back to about 1 GB instead of staying on the 3.5-4.7 GB a fixed -Xms/-Xmx left committed.
/// What these tests hold down is that each one only reaches a Java that knows it, because a JVM
/// given an option it does not recognise does not start at all.
/// </remarks>
public class MemoryProfileTests
{
    private static readonly JavaService.JavaRuntime Java25 = new(25, false, true);

    private static MemoryInputs Inputs(ServerConfig config, JavaService.JavaRuntime? java = null, int mods = 0,
        long totalMb = 16384, long othersMb = 0, bool dedicated = false) =>
        new(config, java ?? Java25, mods, totalMb, othersMb, dedicated);

    private static ServerConfig Automatic() => new() { MemoryMode = MemoryMode.Automatic };

    // --- the mode in effect ---

    [Fact]
    public void AServerThatNeverChoseRunsAutomaticAndShowsTheNotice()
    {
        var config = new ServerConfig();   // MemoryMode null: saved before the choice existed

        Assert.Equal(MemoryMode.Automatic, MemoryProfile.Effective(config));
        Assert.True(MemoryProfile.ShowsMigrationNotice(config));
    }

    [Fact]
    public void ChoosingEitherModeEndsTheNotice()
    {
        Assert.False(MemoryProfile.ShowsMigrationNotice(new ServerConfig { MemoryMode = MemoryMode.Automatic }));
        Assert.False(MemoryProfile.ShowsMigrationNotice(new ServerConfig { MemoryMode = MemoryMode.Manual }));
    }

    [Fact]
    public void ArgumentsThatSizeTheMemoryOutrankTheSavedMode()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Automatic, ExtraJvmArgs = "-Xmx8G" };

        Assert.Equal(MemoryMode.Manual, MemoryProfile.Effective(config));
        // Nothing was moved over for this server, so there is nothing to explain.
        Assert.False(MemoryProfile.ShowsMigrationNotice(new ServerConfig { ExtraJvmArgs = "-Xmx8G" }));
    }

    [Fact]
    public void ManualIsExactlyWhatTheAppAlwaysPassed()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Manual, MinRamGb = 3, MaxRamGb = 6 };

        var plan = MemoryProfile.Plan(Inputs(config));

        Assert.Equal(MemoryMode.Manual, plan.Mode);
        Assert.Equal(new[] { "-Xms3G", "-Xmx6G" }, plan.Flags);
        Assert.Equal(6144, plan.CeilingMb);
    }

    // --- the flags for each Java ---

    [Fact]
    public void Java25GetsTheWholeProfile()
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic()));

        Assert.Equal(new[]
        {
            "-Xms128M", "-Xmx4096M", "-XX:G1PeriodicGCInterval=15000", "-XX:MinHeapFreeRatio=10",
            "-XX:MaxHeapFreeRatio=30", "-XX:+UseCompactObjectHeaders"
        }, plan.Flags);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(21)]
    [InlineData(24)]   // experimental there: it would need an unlock flag
    public void BeforeJava25ThereAreNoCompactHeaders(int major)
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), new JavaService.JavaRuntime(major, false, true)));

        Assert.Contains("-XX:G1PeriodicGCInterval=15000", plan.Flags);
        Assert.DoesNotContain("-XX:+UseCompactObjectHeaders", plan.Flags);
    }

    [Theory]
    [InlineData(0)]    // could not be asked
    [InlineData(8)]
    [InlineData(11)]
    public void BeforeJava12OnlyTheHeapSizeIsGiven(int major)
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), new JavaService.JavaRuntime(major, false, true)));

        Assert.Equal(new[] { "-Xms128M", "-Xmx4096M" }, plan.Flags);
    }

    [Fact]
    public void OpenJ9GetsOnlyTheHeapSize()
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), new JavaService.JavaRuntime(25, true, true)));

        Assert.Equal(new[] { "-Xms128M", "-Xmx4096M" }, plan.Flags);
    }

    [Fact]
    public void TheUsersOwnHeaderFlagIsNotContradicted()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Automatic, ExtraJvmArgs = "-XX:-UseCompactObjectHeaders" };

        var plan = MemoryProfile.Plan(Inputs(config));

        Assert.Equal(MemoryMode.Automatic, plan.Mode);
        Assert.DoesNotContain("-XX:+UseCompactObjectHeaders", plan.Flags);
    }

    [Fact]
    public void A32BitJavaIsCappedAt1Gb()
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), new JavaService.JavaRuntime(8, false, false)));

        Assert.Equal(1024, plan.CeilingMb);
        Assert.True(plan.CappedTo32Bit);
    }

    [Fact]
    public void TheFabricLineOnJava25IsTheMeasuredOne()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Automatic, JarFile = "fabric-server.jar" };
        var plan = MemoryProfile.Plan(Inputs(config));

        var args = ServerProcessManager.BuildJavaArguments(config, 25, plan.Flags);

        Assert.Equal("-Xms128M -Xmx4096M -XX:G1PeriodicGCInterval=15000 -XX:MinHeapFreeRatio=10 " +
                     "-XX:MaxHeapFreeRatio=30 -XX:+UseCompactObjectHeaders --enable-native-access=ALL-UNNAMED " +
                     "-jar \"fabric-server.jar\" nogui", args);
    }

    [Fact]
    public void ARefusedSetLosesCompactHeadersFirstThenTheG1Options()
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic()));

        var fewer = MemoryProfile.WithoutLastOptionalFlag(plan)!;
        Assert.DoesNotContain("-XX:+UseCompactObjectHeaders", fewer.Flags);
        Assert.Contains("-XX:MaxHeapFreeRatio=30", fewer.Flags);

        var fewest = MemoryProfile.WithoutLastOptionalFlag(fewer)!;
        Assert.Equal(new[] { "-Xms128M", "-Xmx4096M" }, fewest.Flags);

        Assert.Null(MemoryProfile.WithoutLastOptionalFlag(fewest));
    }

    // --- the ceiling ---

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(30, 4096)]
    [InlineData(31, 6144)]
    [InlineData(100, 6144)]
    [InlineData(101, 8192)]
    public void TheEstimateGrowsWithTheMods(int mods, long ceilingMb)
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), mods: mods, totalMb: 65536));

        Assert.Equal(ceilingMb, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Estimated, plan.Reason);
    }

    [Fact]
    public void AServerMovedOverKeepsTheLargerCeilingItHad()
    {
        var config = new ServerConfig { MaxRamGb = 6 };   // never chose a mode

        var plan = MemoryProfile.Plan(Inputs(config));

        Assert.Equal(6144, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.KeptPrevious, plan.Reason);
    }

    [Fact]
    public void AServerThatChoseAutomaticIsNotHeldToItsOldNumbers()
    {
        var config = new ServerConfig { MemoryMode = MemoryMode.Automatic, MaxRamGb = 12 };

        Assert.Equal(4096, MemoryProfile.Plan(Inputs(config)).CeilingMb);
    }

    [Fact]
    public void TheBudgetIsHalfThePcUnlessItIsOnlyForServers()
    {
        Assert.Equal(8192, MemoryProfile.BudgetMb(16384, dedicated: false));
        Assert.Equal(14336, MemoryProfile.BudgetMb(16384, dedicated: true));
        Assert.Equal(0, MemoryProfile.BudgetMb(0, dedicated: false));   // unknown: no budget
    }

    [Fact]
    public void WhatOtherServersHoldLowersTheCeiling()
    {
        // 8 GB budget, 5 GB taken by the others: 3 GB left, rounded down to 256 MB.
        var plan = MemoryProfile.Plan(Inputs(Automatic(), othersMb: 5000));

        Assert.Equal(3072, plan.CeilingMb);
        Assert.Equal(MemoryCeilingReason.Budget, plan.Reason);
    }

    [Fact]
    public void AFullBudgetNeverStopsAServerFromStarting()
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), mods: 40, othersMb: 8192));

        Assert.Equal(2560, plan.CeilingMb);   // the floor for a server with mods
        Assert.Equal(MemoryCeilingReason.Floor, plan.Reason);
    }

    [Fact]
    public void ADedicatedPcLeavesRoomForMore()
    {
        var plan = MemoryProfile.Plan(Inputs(Automatic(), mods: 150, totalMb: 8192, dedicated: true));

        Assert.Equal(6144, plan.CeilingMb);   // 8 GB − 2 GB, under the 8 GB estimate
    }

    [Fact]
    public void OnlyLoadersWithAModsFolderCountMods()
    {
        Assert.True(MemoryProfile.HasMods(ServerType.Fabric));
        Assert.True(MemoryProfile.HasMods(ServerType.NeoForge));
        Assert.False(MemoryProfile.HasMods(ServerType.Paper));
        Assert.False(MemoryProfile.HasMods(ServerType.Vanilla));
    }
}

/// <summary>The memory set aside by the servers already running.</summary>
public class MemoryBudgetTests
{
    [Fact]
    public void AServerDoesNotCountAgainstItself()
    {
        var a = "budget-a-" + Guid.NewGuid().ToString("N");
        var b = "budget-b-" + Guid.NewGuid().ToString("N");
        try
        {
            MemoryBudget.Reserve(a, 4096);
            MemoryBudget.Reserve(b, 2048);

            Assert.True(MemoryBudget.ReservedByOthersMb(a) >= 2048);
            Assert.Equal(MemoryBudget.ReservedByOthersMb(a) - 2048 + 4096, MemoryBudget.ReservedByOthersMb(b));

            MemoryBudget.Release(b);
            Assert.Equal(MemoryBudget.ReservedByOthersMb(b) - 4096, MemoryBudget.ReservedByOthersMb(a));
        }
        finally
        {
            MemoryBudget.Release(a);
            MemoryBudget.Release(b);
        }
    }
}
