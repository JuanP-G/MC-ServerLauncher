using System.IO.Compression;
using McServerLauncher.Localization;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Installing or updating a mod leaves exactly one copy of it: the new one.
/// </summary>
/// <remarks>
/// <para>
/// An update used to remove only the row it was started from, and an install only what Modrinth
/// recognised. An older duplicate under another name stayed, so did a disabled copy, so did any jar
/// the store never heard of — and when the store did not answer at all, every old copy stayed. Each
/// one is junk on the server's disk or, if it is enabled, a second copy of the mod that the loader
/// refuses to start with.
/// </para>
/// <para>
/// Real jars in a real temporary folder, because what decides it is the id inside each jar and what
/// happens to files on disk; the store's answer is passed in as the dictionary it would produce.
/// </para>
/// </remarks>
public class UpdateCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-cleanup-" + Guid.NewGuid().ToString("N"));

    public UpdateCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder that outlives the run is harmless */ }
    }

    private string At(string name) => Path.Combine(_dir, name);

    /// <summary>A Fabric jar that says it is <paramref name="id"/>, optionally carrying other mods inside.</summary>
    private string FabricJar(string fileName, string? id, string? name = null, params string[] bundledIds)
    {
        var path = At(fileName);
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            manifest.Write("Manifest-Version: 1.0\n");

        if (id is not null)
        {
            var named = name is null ? "" : $", \"name\": \"{name}\"";
            using var entry = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            entry.Write($"{{\"schemaVersion\": 1, \"id\": \"{id}\"{named}}}");
        }

        foreach (var bundled in bundledIds)
        {
            using var inner = new MemoryStream();
            using (var innerZip = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = new StreamWriter(innerZip.CreateEntry("fabric.mod.json").Open()))
                entry.Write($"{{\"schemaVersion\": 1, \"id\": \"{bundled}\"}}");

            using var nested = zip.CreateEntry($"META-INF/jars/{bundled}.jar").Open();
            nested.Write(inner.ToArray());
        }

        return path;
    }

    /// <summary>A jar with a single metadata file of any format.</summary>
    private string JarWith(string fileName, string entryName, string content)
    {
        var path = At(fileName);
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        using var entry = new StreamWriter(zip.CreateEntry(entryName).Open());
        entry.Write(content);
        return path;
    }

    private string[] FilesLeft() =>
        Directory.EnumerateFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    // --- Which jars are another copy ---

    [Fact]
    public void TheSameProjectUnderAnotherNameIsAnotherCopy()
    {
        var old = FabricJar("sodium-0.5.jar", id: null);   // says nothing; only the store knows it
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        var victims = ContentReplacement.OthersOf(_dir, fresh, "AANobbMI",
            new Dictionary<string, string> { [old] = "AANobbMI" });

        Assert.Equal(new[] { old }, victims);
    }

    [Fact]
    public void WithoutTheStoreTheJarsOwnIdStillFindsTheOldVersion()
    {
        // Offline, GetVersionsByHashAsync answers nothing at all, and the old jar used to stay.
        var old = FabricJar("sodium-0.5.jar", "sodium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        Assert.Equal(new[] { old }, ContentReplacement.OthersOf(_dir, fresh));
    }

    [Fact]
    public void ADisabledCopyUnderAnotherNameIsAnotherCopy()
    {
        var old = FabricJar("sodium-0.5.jar.disabled", "sodium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        Assert.Equal(new[] { old }, ContentReplacement.OthersOf(_dir, fresh));
    }

    [Fact]
    public void ADisabledFileOfTheNewNameGoesEvenIfItSaysNothing()
    {
        var stale = FabricJar("thing.jar.disabled", id: null);
        var fresh = FabricJar("thing.jar", id: null);

        Assert.Equal(new[] { stale }, ContentReplacement.OthersOf(_dir, fresh));
    }

    [Fact]
    public void AModThatOnlyCarriesTheNewModInsideItIsNotACopyOfIt()
    {
        // It provides fabric-api, but it is not fabric-api. Taking "provides" for "is" would delete it.
        FabricJar("bigmod.jar", "bigmod", null, "fabric-api");
        var fresh = FabricJar("fabric-api-0.161.jar", "fabric-api");

        Assert.Empty(ContentReplacement.OthersOf(_dir, fresh));
    }

    [Fact]
    public void JarsThatSayNothingAreNeverMatchedToEachOther()
    {
        FabricJar("handmade.jar", id: null);
        var fresh = FabricJar("other.jar", id: null);

        Assert.Empty(ContentReplacement.OthersOf(_dir, fresh));
    }

    [Fact]
    public void OtherModsAreLeftAlone()
    {
        FabricJar("lithium.jar", "lithium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        Assert.Empty(ContentReplacement.OthersOf(_dir, fresh));
    }

    [Fact]
    public void TheNewJarIsNeverItsOwnVictim()
    {
        var fresh = FabricJar("Sodium.jar", "sodium");
        var folder = new[] { new ContentManifest.Entry(At("sodium.JAR"), true, ContentManifest.Read(fresh)) };

        // Same file, spelled differently: Windows paths do not care about case.
        Assert.Empty(ContentReplacement.Select(folder, At("SODIUM.jar"), "sodium", null, null));
    }

    // --- Removing them ---

    [Fact]
    public void EveryCopyIsRemoved()
    {
        var a = FabricJar("a.jar", "x");
        var b = FabricJar("b.jar.disabled", "x");

        var (result, removed) = ContentReplacement.RemoveAllOrNothing(new[] { a, b }, rollbackPath: null);

        Assert.Equal(ContentReplacement.RemoveResult.Done, result);
        Assert.Equal(new[] { a, b }, removed);
        Assert.Empty(FilesLeft());
    }

    [Fact]
    public void ACopyInUseMeansNothingIsRemovedAndTheNewJarGoesBack()
    {
        // Windows refuses to rename a file another process holds open, which is what a running
        // server does to its jars. Elsewhere nothing refuses, so there is nothing to test.
        if (!OperatingSystem.IsWindows()) return;

        var first = FabricJar("a.jar", "x");
        var held = FabricJar("b.jar", "x");
        var fresh = FabricJar("c.jar", "x");

        ContentReplacement.RemoveResult result;
        IReadOnlyList<string> removed;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            (result, removed) = ContentReplacement.RemoveAllOrNothing(new[] { first, held }, fresh);

        Assert.Equal(ContentReplacement.RemoveResult.Locked, result);
        Assert.Empty(removed);
        // The server keeps both of the files it had, and the half-done install is undone.
        Assert.Equal(new[] { "a.jar", "b.jar" }, FilesLeft());
    }

    [Fact]
    public void ANewJarThatReplacedAFileOfItsNameIsNotRolledBack()
    {
        if (!OperatingSystem.IsWindows()) return;

        var held = FabricJar("old.jar", "x");
        var fresh = FabricJar("x.jar", "x");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            ContentReplacement.RemoveAllOrNothing(new[] { held }, rollbackPath: null);

        // Deleting it would leave no copy of the mod at all.
        Assert.Contains("x.jar", FilesLeft());
    }

    [Fact]
    public void WhatAnInterruptedRemovalLeftAsideIsSwept()
    {
        File.WriteAllText(At("old.jar" + ContentReplacement.RemovingSuffix), "");
        FabricJar("keep.jar", "keep");

        ContentReplacement.SweepLeftovers(_dir);

        Assert.Equal(new[] { "keep.jar" }, FilesLeft());
    }

    // --- An update, end to end on disk ---

    [Fact]
    public void ADisabledModStaysDisabledWithOneCopy()
    {
        var old = FabricJar("sodium-0.5.jar.disabled", "sodium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        var (outcome, removed) = ContentReplacement.ApplyDownloadedUpdate(
            _dir, fresh, old, wasDisabled: true, downloadReplacedAFile: false, null, null);

        Assert.Equal(ContentReplacement.UpdateOutcome.Updated, outcome);
        Assert.Equal(new[] { old }, removed);
        Assert.Equal(new[] { "sodium-0.6.jar.disabled" }, FilesLeft());
    }

    [Fact]
    public void AnOlderDuplicateGoesWithTheVersionBeingUpdated()
    {
        var row = FabricJar("sodium-0.5.jar", "sodium");
        FabricJar("sodium-0.4.jar", "sodium");
        FabricJar("lithium.jar", "lithium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        var (_, removed) = ContentReplacement.ApplyDownloadedUpdate(
            _dir, fresh, row, wasDisabled: false, downloadReplacedAFile: false, null, null);

        Assert.Equal(2, removed.Count);
        Assert.Equal(new[] { "lithium.jar", "sodium-0.6.jar" }, FilesLeft());
    }

    [Fact]
    public void UpdatingADisabledRowKeepsTheModOnWhenAnotherCopyWasOn()
    {
        // The server was running the enabled copy; the update is of the mod it actually used.
        var row = FabricJar("sodium-0.5.jar.disabled", "sodium");
        FabricJar("sodium-0.4.jar", "sodium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        ContentReplacement.ApplyDownloadedUpdate(
            _dir, fresh, row, wasDisabled: true, downloadReplacedAFile: false, null, null);

        Assert.Equal(new[] { "sodium-0.6.jar" }, FilesLeft());
    }

    [Fact]
    public void UpdatingADisabledRowOverTheEnabledCopyOfTheSameNameKeepsItOn()
    {
        // The server runs Geyser-Fabric.jar; a stale Geyser-Fabric.jar.disabled is updated, and the
        // update's file has the enabled copy's name, so the download overwrote it. The only other
        // copy left is the disabled one, and the update used to switch the mod off.
        var row = FabricJar("Geyser-Fabric.jar.disabled", "geyser-fabric");
        var fresh = FabricJar("Geyser-Fabric.jar", "geyser-fabric");   // what the download overwrote

        ContentReplacement.ApplyDownloadedUpdate(
            _dir, fresh, row, wasDisabled: true, downloadReplacedAFile: true, null, null);

        Assert.Equal(new[] { "Geyser-Fabric.jar" }, FilesLeft());
    }

    [Fact]
    public void TheRowsOwnFileGoesEvenWhenNothingTiesItToTheNewOne()
    {
        // A jar that declares no id, and a store that did not answer: the row is still the mod the
        // user asked to update.
        var row = FabricJar("mystery-1.jar", id: null);
        var fresh = FabricJar("mystery-2.jar", id: null);

        ContentReplacement.ApplyDownloadedUpdate(
            _dir, fresh, row, wasDisabled: false, downloadReplacedAFile: false, null, null);

        Assert.Equal(new[] { "mystery-2.jar" }, FilesLeft());
    }

    [Fact]
    public void ANewVersionWithTheSameFileNameRemovesNothing()
    {
        var same = FabricJar("spark.jar", "spark");

        var (outcome, removed) = ContentReplacement.ApplyDownloadedUpdate(
            _dir, same, same, wasDisabled: false, downloadReplacedAFile: true, null, null);

        Assert.Equal(ContentReplacement.UpdateOutcome.Updated, outcome);
        Assert.Empty(removed);
        Assert.Equal(new[] { "spark.jar" }, FilesLeft());
    }

    [Fact]
    public void AnUpdateBlockedByTheServerChangesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var row = FabricJar("sodium-0.5.jar", "sodium");
        var fresh = FabricJar("sodium-0.6.jar", "sodium");

        ContentReplacement.UpdateOutcome outcome;
        using (new FileStream(row, FileMode.Open, FileAccess.Read, FileShare.None))
            (outcome, _) = ContentReplacement.ApplyDownloadedUpdate(
                _dir, fresh, row, wasDisabled: false, downloadReplacedAFile: false, null, null);

        Assert.Equal(ContentReplacement.UpdateOutcome.NeedsStop, outcome);
        Assert.Equal(new[] { "sodium-0.5.jar" }, FilesLeft());
    }

    // --- The content installers ---

    [Fact]
    public void AReinstalledPluginReplacesTheBuildThatWasThere()
    {
        // Floodgate for Paper comes from GeyserMC's site, which Modrinth's lookup has never heard of:
        // only the plugin's own name can say the old build is the same plugin.
        JarWith("floodgate-spigot-100.jar", "plugin.yml", "name: floodgate\nversion: 2.2.3\n");
        var fresh = JarWith("floodgate-spigot-120.jar", "plugin.yml", "name: floodgate\nversion: 2.2.4\n");

        var removed = ContentInstall.ReplacePrevious(_dir, fresh, "Floodgate");

        Assert.Single(removed);
        Assert.Equal(new[] { "floodgate-spigot-120.jar" }, FilesLeft());
    }

    [Fact]
    public void FabricApiIsNotNeededWhenAnEnabledJarProvidesIt()
    {
        FabricJar("fabric-api-0.160.jar", "fabric-api");

        Assert.False(HydraulicService.NeedsFabricApi(_dir));
    }

    [Fact]
    public void ADisabledFabricApiDoesNotCount()
    {
        FabricJar("fabric-api-0.160.jar.disabled", "fabric-api");

        Assert.True(HydraulicService.NeedsFabricApi(_dir));
    }

    [Fact]
    public void FabricApiCarriedInsideAnotherModCounts()
    {
        FabricJar("bundle.jar", "bundle", null, "fabric-api");

        Assert.False(HydraulicService.NeedsFabricApi(_dir));
    }

    [Fact]
    public void AnEmptyFolderNeedsFabricApi() => Assert.True(HydraulicService.NeedsFabricApi(_dir));

    // --- What the user is told ---

    /// <remarks>
    /// Only that it was updated. Taking out the old version is what updating is, and listing the files
    /// deleted along the way read as if something unusual had happened.
    /// </remarks>
    [Fact]
    public void UpdateAllSaysHowManyWereUpdatedAndNothingAboutOldFiles() =>
        Assert.Equal(string.Format(Localizer.Get("Msg_UpdateAllDoneFmt"), 3),
            ServerModsViewModel.UpdateAllSummary(3, Array.Empty<string>(), stoppedByServer: false));

}
