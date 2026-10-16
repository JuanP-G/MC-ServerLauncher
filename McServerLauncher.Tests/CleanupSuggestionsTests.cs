using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The "you can clean up" panel: which libraries nothing needs, which mods have been off too long,
/// and that the panel only ever suggests.
/// </summary>
/// <remarks>
/// <para>
/// The rules are tested on jars described in code, because what they decide is a graph question —
/// who refers to whom — and a folder of real jars would only make the cases harder to read. The jar
/// metadata that feeds them is tested against real archives in <see cref="ContentManifestTests"/>.
/// </para>
/// <para>
/// Every rule leans towards keeping a jar. Suggesting the removal of something a mod uses is the one
/// mistake that would make people stop trusting the panel, so most tests here are about what must
/// <em>not</em> be suggested.
/// </para>
/// </remarks>
public class CleanupSuggestionsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-clean-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousLedgerRoot = ContentLedgerStore.RootDirectory;

    public CleanupSuggestionsTests()
    {
        Directory.CreateDirectory(_root);
        ContentLedgerStore.RootDirectory = Path.Combine(_root, "ledgers");
    }

    public void Dispose()
    {
        ContentLedgerStore.RootDirectory = _previousLedgerRoot;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A jar as the advisor sees it.</summary>
    private static CleanupAdvisor.Jar Jar(string id, bool library = false, bool enabled = true, string? name = null,
        string[]? refs = null, string? project = null, string[]? storeDeps = null, string[]? provides = null)
    {
        var file = Path.Combine(Path.GetTempPath(), "mods", id + ".jar" + (enabled ? "" : ".disabled"));
        var manifest = new ContentManifest.Manifest(Path.GetFileName(file), provides ?? new[] { id }, Array.Empty<string>())
        {
            Id = id,
            Name = name,
            References = refs ?? Array.Empty<string>()
        };
        return new CleanupAdvisor.Jar(file, enabled, manifest, project, library, storeDeps ?? Array.Empty<string>());
    }

    private static IReadOnlyList<CleanupAdvisor.Orphan> Orphans(params CleanupAdvisor.Jar[] jars) =>
        CleanupAdvisor.FindOrphanLibraries(jars, new Dictionary<string, List<string>>(), storeAnswered: true);

    private static string[] Ids(IEnumerable<CleanupAdvisor.Orphan> orphans) =>
        orphans.Select(o => o.Jar.Manifest.Id!).OrderBy(i => i).ToArray();

    // --- What a library is ---

    [Fact]
    public void JeiIsNotALibraryEvenThoughTheStoreFilesItAsOne()
    {
        // Modrinth files JEI under "library, utility". Calling it a library would offer to remove
        // the recipe viewer people open every day, because no mod "needs" it.
        Assert.False(CleanupAdvisor.IsLibrary(Jar("jei").Manifest, new[] { "library", "utility" }));
    }

    [Fact]
    public void SomethingFiledOnlyAsALibraryIsOne() =>
        Assert.True(CleanupAdvisor.IsLibrary(Jar("some-lib").Manifest, new[] { "library" }));

    [Fact]
    public void MalilibIsALibraryByName()
    {
        // Filed exactly like JEI on Modrinth; only the hand-written list tells them apart.
        Assert.True(CleanupAdvisor.IsLibrary(Jar("malilib").Manifest, new[] { "library", "utility" }));
    }

    [Fact]
    public void AJarTheStoreDoesNotKnowIsNotALibrary() =>
        Assert.False(CleanupAdvisor.IsLibrary(Jar("handmade").Manifest, null));

    [Fact]
    public void AWorldGenerationLibraryIsNotTakenForOneByName() =>
        // Datapacks in world/datapacks need lithostitched, and the panel cannot see them: by name
        // alone it would be offered as unused while the world uses it.
        Assert.False(CleanupAdvisor.IsLibrary(Jar("lithostitched").Manifest, null));

    // --- Which libraries are orphans ---

    [Fact]
    public void ALibraryNothingUsesIsSuggested()
    {
        var found = Orphans(Jar("lithium"), Jar("malilib", library: true));

        Assert.Equal(new[] { "malilib" }, Ids(found));
        Assert.Equal(CleanupAdvisor.OrphanReason.NoKnownUser, found.Single().Reason);
    }

    [Fact]
    public void ALibraryAModUsesIsKept() =>
        Assert.Empty(Orphans(Jar("litematica", refs: new[] { "malilib" }), Jar("malilib", library: true)));

    [Fact]
    public void ALibraryOnlyADisabledModUsesIsKept()
    {
        // The user may turn it back on, and then its library has to be there.
        Assert.Empty(Orphans(Jar("litematica", enabled: false, refs: new[] { "malilib" }), Jar("malilib", library: true)));
    }

    [Fact]
    public void ALibraryOnlyTheStoreSaysIsUsedIsKept()
    {
        // The jar's own metadata forgot it; Modrinth's dependency list did not.
        Assert.Empty(Orphans(
            Jar("waystones", storeDeps: new[] { "balm-project" }),
            Jar("balm", library: true, project: "balm-project")));
    }

    [Fact]
    public void FabricMeansFabricApi()
    {
        // Old Fabric mods name fabric-api by its old id.
        Assert.Empty(Orphans(Jar("oldmod", refs: new[] { "fabric" }), Jar("fabric-api", library: true)));
    }

    /// <summary>A library that carries <paramref name="bundled"/> inside its jar.</summary>
    private static CleanupAdvisor.Jar Bundling(string id, params string[] bundled)
    {
        var jar = Jar(id, library: true);
        return jar with
        {
            Manifest = jar.Manifest with
            {
                Provides = new[] { id }.Concat(bundled).ToList(), OwnProvides = new[] { id }
            }
        };
    }

    [Fact]
    public void CarryingAFabricApiModuleDoesNotMakeALibraryUsed()
    {
        // Found on a real server: malilib bundles fabric-resource-loader-v1, so every mod using that
        // module looked like it used malilib, and malilib was never suggested. The module is
        // fabric-api's.
        var found = Orphans(
            Jar("shulkerboxtooltip", refs: new[] { "fabric-resource-loader-v1" }),
            Bundling("malilib", "fabric-resource-loader-v1", "fabric-api-base"),
            Jar("fabric-api", library: true));

        Assert.Equal(new[] { "malilib" }, Ids(found));
    }

    [Fact]
    public void ANameOnlyALibraryCarriesStillKeepsIt()
    {
        // Nothing installed *is* yumi-core; the library that carries it is the only one that can
        // satisfy the reference, so it stays.
        Assert.Empty(Orphans(Jar("lights", refs: new[] { "yumi-core" }), Bundling("some-lib", "yumi-core")));
    }

    [Fact]
    public void ALibraryUsedThroughAnotherOfItsIdsIsKept()
    {
        Assert.Empty(Orphans(
            Jar("shop", refs: new[] { "cloth-config2" }),
            Jar("cloth-config", library: true, provides: new[] { "cloth-config", "cloth-config2" })));
    }

    [Fact]
    public void AChainIsKeptAsAWhole()
    {
        Assert.Empty(Orphans(
            Jar("mod", refs: new[] { "lib-a" }),
            Jar("lib-a", library: true, refs: new[] { "lib-b" }),
            Jar("lib-b", library: true)));
    }

    [Fact]
    public void AChainNothingUsesGoesAsAWhole()
    {
        var found = Orphans(
            Jar("lithium"),
            Jar("lib-a", library: true, refs: new[] { "lib-b" }),
            Jar("lib-b", library: true));

        Assert.Equal(new[] { "lib-a", "lib-b" }, Ids(found));
    }

    [Fact]
    public void LibrariesThatOnlyNeedEachOtherDoNotKeepEachOtherAlive()
    {
        var found = Orphans(
            Jar("lib-a", library: true, refs: new[] { "lib-b" }),
            Jar("lib-b", library: true, refs: new[] { "lib-a" }));

        Assert.Equal(new[] { "lib-a", "lib-b" }, Ids(found));
    }

    [Fact]
    public void WithoutTheStoreNothingIsSuggested()
    {
        // Offline, the dependencies only Modrinth knows are missing, so "unused" cannot be trusted.
        Assert.Empty(CleanupAdvisor.FindOrphanLibraries(
            new[] { Jar("malilib", library: true) }, new Dictionary<string, List<string>>(), storeAnswered: false));
    }

    // --- Why ---

    [Fact]
    public void ALibraryWhoseModWasRemovedSaysWhichModItWas()
    {
        var ledger = new ContentLedger();

        // Before: litematica uses malilib, and that is written down.
        CleanupAdvisor.RecordDependents(ledger, new[]
        {
            Jar("litematica", name: "Litematica", refs: new[] { "malilib" }), Jar("malilib", library: true)
        });

        // After: litematica is gone.
        var found = CleanupAdvisor.FindOrphanLibraries(
            new[] { Jar("malilib", library: true) }, ledger.DependentsByLibrary, storeAnswered: true);

        var orphan = Assert.Single(found);
        Assert.Equal(CleanupAdvisor.OrphanReason.DependentsRemoved, orphan.Reason);
        Assert.Equal(new[] { "Litematica" }, orphan.FormerUsers);
    }

    [Fact]
    public void ALibraryWhoseUserIsItselfUnneededSaysSo()
    {
        var ledger = new ContentLedger();
        var a = Jar("lib-a", library: true, name: "Lib A", refs: new[] { "lib-b" });
        CleanupAdvisor.RecordDependents(ledger, new[] { Jar("mod", refs: new[] { "lib-a" }), a, Jar("lib-b", library: true) });

        var found = CleanupAdvisor.FindOrphanLibraries(new[] { a, Jar("lib-b", library: true) },
            ledger.DependentsByLibrary, storeAnswered: true);

        Assert.Equal(CleanupAdvisor.OrphanReason.DependentsAlsoOrphan,
            found.Single(o => o.Jar.Manifest.Id == "lib-b").Reason);
    }

    [Fact]
    public void TheRecordOfALibraryThatIsGoneIsForgotten()
    {
        var ledger = new ContentLedger();
        ledger.DependentsByLibrary["old-lib"] = new List<string> { "Something" };

        CleanupAdvisor.RecordDependents(ledger, new[] { Jar("lithium") });

        Assert.Empty(ledger.DependentsByLibrary);
    }

    [Fact]
    public void RecordingTheSameDependentsAgainChangesNothing()
    {
        // It runs after every toggle and delete, and the ledger was written every time.
        var ledger = new ContentLedger();
        var jars = new[] { Jar("mod", refs: new[] { "lib" }), Jar("lib", library: true) };

        Assert.True(CleanupAdvisor.RecordDependents(ledger, jars));
        Assert.False(CleanupAdvisor.RecordDependents(ledger, jars));
    }

    [Fact]
    public void TheLedgerOfARemovedServerIsDeleted()
    {
        var kept = Path.Combine(ContentLedgerStore.RootDirectory, "kept");
        var gone = Path.Combine(ContentLedgerStore.RootDirectory, "gone");
        Directory.CreateDirectory(kept);
        Directory.CreateDirectory(gone);

        ContentLedgerStore.PruneOrphans(new[] { "kept" });

        Assert.True(Directory.Exists(kept));
        Assert.False(Directory.Exists(gone));
    }

    // --- Off for a month ---

    [Fact]
    public void OffFor29DaysIsNotSuggestedAndFor31Is()
    {
        var jar = Jar("shogi", enabled: false);
        var ledger = new ContentLedger();

        ledger.DisabledSinceUtc["shogi.jar"] = Now.AddDays(-29);
        Assert.Empty(CleanupAdvisor.FindLongDisabled(new[] { jar }, ledger, Now, CleanupAdvisor.DisabledThreshold));

        ledger.DisabledSinceUtc["shogi.jar"] = Now.AddDays(-31);
        var off = Assert.Single(CleanupAdvisor.FindLongDisabled(new[] { jar }, ledger, Now, CleanupAdvisor.DisabledThreshold));
        Assert.Equal(31, off.Days);
        Assert.False(off.SinceIsFirstSeen);
    }

    [Fact]
    public void AModAlreadyOffStartsCountingWhenFirstSeen()
    {
        var ledger = new ContentLedger();

        Assert.True(CleanupAdvisor.Reconcile(ledger, new[] { ("shogi.jar", false), ("lithium.jar", true) }, Now));

        Assert.Equal(Now, ledger.DisabledSinceUtc["shogi.jar"]);
        Assert.Contains("shogi.jar", ledger.DisabledFirstSeen);
        Assert.False(ledger.DisabledSinceUtc.ContainsKey("lithium.jar"));

        // Seen again later: the date it was first seen stays.
        Assert.False(CleanupAdvisor.Reconcile(ledger, new[] { ("shogi.jar", false) }, Now.AddDays(5)));
        Assert.Equal(Now, ledger.DisabledSinceUtc["shogi.jar"]);
    }

    [Fact]
    public void TurningAModBackOnOrDeletingItForgetsTheDate()
    {
        var ledger = new ContentLedger();
        CleanupAdvisor.Reconcile(ledger, new[] { ("a.jar", false), ("b.jar", false) }, Now);

        CleanupAdvisor.Reconcile(ledger, new[] { ("a.jar", true) }, Now);

        Assert.Empty(ledger.DisabledSinceUtc);
        Assert.Empty(ledger.DisabledFirstSeen);
    }

    [Fact]
    public void TurningAModOffInTheAppRecordsTheRealDay()
    {
        var ledger = new ContentLedger();
        CleanupAdvisor.Reconcile(ledger, new[] { ("a.jar", false) }, Now);   // first seen

        CleanupAdvisor.RecordToggle(ledger, "a.jar", nowEnabled: false, Now.AddDays(2));

        Assert.Equal(Now.AddDays(2), ledger.DisabledSinceUtc["a.jar"]);
        Assert.DoesNotContain("a.jar", ledger.DisabledFirstSeen);
    }

    [Fact]
    public void AnUpdateKeepsTheDayAModWasTurnedOff()
    {
        var ledger = new ContentLedger();
        ledger.DisabledSinceUtc["shogi-1.jar"] = Now.AddDays(-40);

        CleanupAdvisor.CarryDisabledDate(ledger, "shogi-1.jar", "shogi-2.jar");

        Assert.Equal(Now.AddDays(-40), ledger.DisabledSinceUtc["shogi-2.jar"]);
        Assert.False(ledger.DisabledSinceUtc.ContainsKey("shogi-1.jar"));
    }

    // --- "Not now" ---

    [Fact]
    public void NotNowHidesASuggestionForThirtyDays()
    {
        var ledger = new ContentLedger();
        ledger.DismissedUtc["orphan:malilib"] = Now;

        Assert.True(CleanupAdvisor.IsSnoozed(ledger, "orphan:malilib", Now.AddDays(29)));
        Assert.False(CleanupAdvisor.IsSnoozed(ledger, "orphan:malilib", Now.AddDays(31)));

        // And the expired entry is dropped from the file.
        CleanupAdvisor.Reconcile(ledger, Array.Empty<(string, bool)>(), Now.AddDays(31));
        Assert.Empty(ledger.DismissedUtc);
    }

    // --- The ledger on disk ---

    [Fact]
    public void TheLedgerSurvivesBeingSavedAndOpenedAgain()
    {
        var dir = Path.Combine(_root, "one");
        var store = new ContentLedgerStore(dir);
        store.Current.DisabledSinceUtc["Shogi.jar"] = Now;
        store.Current.DependentsByLibrary["malilib"] = new List<string> { "Litematica" };
        store.Save();

        var again = new ContentLedgerStore(dir);

        // Case-insensitive again after the round trip, which JSON cannot record by itself.
        Assert.Equal(Now, again.Current.DisabledSinceUtc["shogi.jar"]);
        Assert.Equal(new[] { "Litematica" }, again.Current.DependentsByLibrary["MALILIB"]);
    }

    [Fact]
    public void ACorruptLedgerIsAnEmptyOne()
    {
        var dir = Path.Combine(_root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ledger.json"), "{ not json");

        Assert.Empty(new ContentLedgerStore(dir).Current.DisabledSinceUtc);
    }

    // --- What the panel says ---

    [Fact]
    public void TheDayIsWrittenTheWayTheLanguageWritesIt()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
            var day = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal("2 de agosto", ServerModsViewModel.FormatDay(day, today: new DateTime(2026, 10, 14)));

            // A year ago is not "2 August": the year is said when it is not this one.
            Assert.Equal("2 de agosto de 2026", ServerModsViewModel.FormatDay(day, today: new DateTime(2027, 9, 1)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void ADisabledModSaysSinceWhenAndHowLong()
    {
        var since = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var reason = ServerModsViewModel.ReasonFor(new CleanupAdvisor.LongDisabled(Jar("shogi", enabled: false), since, false, 72));

        Assert.Equal(string.Format(Localizer.Get("Cleanup_DisabledSinceFmt"), ServerModsViewModel.FormatDay(since), 72), reason);
    }

    // --- The panel ---

    private ServerModsViewModel Panel(out string mods)
    {
        var folder = Path.Combine(_root, "server");
        mods = Path.Combine(folder, "mods");
        Directory.CreateDirectory(mods);
        return new ServerModsViewModel(new ServerConfig
        {
            Name = "test", FolderPath = folder, Type = ServerType.Fabric, GameVersion = "1.21.1"
        });
    }

    [Fact]
    public void BuildingThePanelWritesNoLedger()
    {
        // Built for every server, tab shown or not, and by tests: nothing may land in the user's
        // application data until the tab is actually on screen.
        Panel(out var mods);
        File.WriteAllText(Path.Combine(mods, "a.jar.disabled"), "");
        Panel(out _).ReloadInstalled();

        Assert.False(Directory.Exists(ContentLedgerStore.RootDirectory));
    }

    [Fact]
    public void RemovingTheTickedRowsDeletesOnlyThose()
    {
        var vm = Panel(out var mods);
        var keep = Path.Combine(mods, "keep.jar");
        var drop = Path.Combine(mods, "drop.jar");
        File.WriteAllText(keep, "");
        File.WriteAllText(drop, "");

        vm.ShowCleanup(new[]
        {
            new CleanupSuggestionViewModel(keep, "keep.jar", "x", "orphan:keep") { IsSelected = false },
            new CleanupSuggestionViewModel(drop, "drop.jar", "x", "orphan:drop")
        });
        Assert.Equal(Localizer.Get("Cleanup_RemoveSelectedOne"), vm.RemoveSelectedText);

        vm.RemoveSelectedCleanupCommand.Execute(null);

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(drop));
        Assert.Contains("drop.jar", vm.UpdateStatus);
    }

    [Fact]
    public void ARowWhoseFileWentAwaySaysSoInsteadOfRemoved()
    {
        // The mod was switched back on (renamed) after the scan: deleting the old path removed
        // nothing and the status said "removed".
        var vm = Panel(out var mods);
        var off = Path.Combine(mods, "foo.jar.disabled");
        File.WriteAllText(off, "");
        vm.ShowCleanup(new[] { new CleanupSuggestionViewModel(off, "foo.jar", "x", "disabled:foo") });
        File.Move(off, Path.Combine(mods, "foo.jar"));

        vm.RemoveSelectedCleanupCommand.Execute(null);

        Assert.True(File.Exists(Path.Combine(mods, "foo.jar")));
        Assert.DoesNotContain(string.Format(Localizer.Get("Cleanup_RemovedFmt"), "foo.jar"), vm.UpdateStatus);
        Assert.Contains(string.Format(Localizer.Get("Cleanup_ChangedFmt"), "foo.jar"), vm.UpdateStatus);
    }

    [Fact]
    public void AFileReplacedUnderTheSameNameIsNotRemoved()
    {
        var vm = Panel(out var mods);
        var jar = Path.Combine(mods, "geyser.jar");
        File.WriteAllText(jar, "old");
        vm.ShowCleanup(new[] { new CleanupSuggestionViewModel(jar, "geyser.jar", "x", "orphan:geyser") });
        File.WriteAllText(jar, "a new version, just installed");

        vm.RemoveSelectedCleanupCommand.Execute(null);

        Assert.True(File.Exists(jar));
    }

    [Fact]
    public void NothingIsRemovedWhileUpdateAllRuns()
    {
        var vm = Panel(out var mods);
        var jar = Path.Combine(mods, "a.jar");
        File.WriteAllText(jar, "");
        vm.ShowCleanup(new[] { new CleanupSuggestionViewModel(jar, "a.jar", "x", "orphan:a") });

        vm.IsUpdatingAll = true;

        Assert.False(vm.RemoveSelectedCleanupCommand.CanExecute(null));
    }

    [Fact]
    public void NothingTickedMeansTheButtonDoesNothing()
    {
        var vm = Panel(out var mods);
        var file = Path.Combine(mods, "a.jar");
        File.WriteAllText(file, "");
        vm.ShowCleanup(new[] { new CleanupSuggestionViewModel(file, "a.jar", "x", "k") { IsSelected = false } });

        Assert.False(vm.RemoveSelectedCleanupCommand.CanExecute(null));
    }

    [Fact]
    public void NotNowEmptiesThePanelAndIsRemembered()
    {
        var vm = Panel(out var mods);
        vm.ShowCleanup(new[] { new CleanupSuggestionViewModel(Path.Combine(mods, "a.jar"), "a.jar", "x", "orphan:a") });

        vm.DismissCleanupCommand.Execute(null);

        Assert.False(vm.HasCleanupSuggestions);
        var serverId = typeof(ServerModsViewModel)
            .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm) as ServerConfig;
        var reopened = new ContentLedgerStore(Path.Combine(ContentLedgerStore.RootDirectory, serverId!.Id));
        Assert.True(reopened.Current.DismissedUtc.ContainsKey("orphan:a"));
    }

    [Fact]
    public void EveryNameThePanelBindsToExists()
    {
        var view = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "ServerModsView.axaml");
        var xaml = File.ReadAllText(view);
        var start = xaml.IndexOf("<Border IsVisible=\"{Binding HasCleanupSuggestions}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "el panel de limpieza ya no está en ServerModsView.axaml");
        var end = xaml.IndexOf("</Border>", start, StringComparison.Ordinal);
        var panel = xaml[start..end];

        var names = Regex.Matches(panel, @"\{Binding\s+!?([A-Za-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var rowNames = new[] { "IsSelected", "FileName", "Reason" };
        foreach (var name in names)
        {
            var owner = rowNames.Contains(name) ? typeof(CleanupSuggestionViewModel) : typeof(ServerModsViewModel);
            Assert.True(owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"{owner.Name} no expone \"{name}\", al que se enlaza el panel de limpieza");
        }

        Assert.Contains("CleanupSuggestions", names);
        Assert.Contains("RemoveSelectedCleanupCommand", names);
        Assert.Contains("DismissCleanupCommand", names);
    }
}
