using McServerLauncher.Models;
using McServerLauncher.Models.Modrinth;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// What the store browser asks for, which page it asks for next, and which of two overlapping
/// searches is the one that counts.
/// </summary>
public class StoreBrowserTests
{
    private static StoreTagDefinition Facet(params string[] facets) => new() { Facets = facets.ToList() };

    private static StoreTagDefinition Words(string words) => new() { Query = words };

    private static SearchResponse Page(int hits, int total) => new()
    {
        TotalHits = total,
        Hits = Enumerable.Range(0, hits).Select(i => new ProjectResult { ProjectId = "p" + i }).ToList(),
    };

    private static ServerConfig Config(ServerType type = ServerType.Fabric, string version = "1.21.1") => new()
    {
        Name = "test",
        FolderPath = Path.GetTempPath(),
        Type = type,
        GameVersion = version,
    };

    // ---- what is asked ----

    [Fact]
    public void FacetCategoriesStackAndTheirWordsStayOutOfTheQuery()
    {
        var (query, facets) = StoreBrowser.BuildQuery("lithium", [Facet("a", "b"), Facet("c")]);

        Assert.Equal("lithium", query);
        Assert.Equal(["a", "b", "c"], facets);
    }

    [Fact]
    public void ACategoryWithNoFacetAddsItsWordsToTheQuery()
    {
        var (query, facets) = StoreBrowser.BuildQuery("magic", [Words("spells")]);

        Assert.Equal("magic spells", query);
        Assert.Empty(facets);
    }

    [Fact]
    public void WithNothingTypedTheCategoryWordsAreTheWholeQuery()
    {
        Assert.Equal("spells", StoreBrowser.BuildQuery("  ", [Words("spells")]).Query);
        Assert.Equal("spells", StoreBrowser.BuildQuery(null, [Words("spells")]).Query);
        Assert.Equal("", StoreBrowser.BuildQuery(null, []).Query);
    }

    [Theory]
    [InlineData(0, "relevance")]
    [InlineData(1, "downloads")]
    [InlineData(7, "relevance")]
    public void TheSortChoiceIsTheStoresName(int selected, string expected) =>
        Assert.Equal(expected, StoreBrowser.SortIndex(selected));

    [Fact]
    public void VanillaAndAServerWithNoVersionHaveNothingToSearchFor()
    {
        var store = new FakeModrinthStore();

        Assert.False(new StoreBrowser(Config(ServerType.Vanilla), store).CanSearch);
        Assert.False(new StoreBrowser(Config(version: ""), store).CanSearch);
        Assert.True(new StoreBrowser(Config(), store).CanSearch);
    }

    // ---- paging ----

    [Fact]
    public async Task LoadMoreAsksForTheNextOffsetAndANewSearchStartsOver()
    {
        var offsets = new List<int>();
        var store = new FakeModrinthStore
        {
            OnSearch = (_, offset, _) => { offsets.Add(offset); return Task.FromResult<SearchResponse?>(Page(20, 100)); },
        };
        var browser = new StoreBrowser(Config(), store);

        await browser.LoadAsync("", [], 0, StoreSideFilter.All, append: false, CancellationToken.None);
        await browser.LoadAsync("", [], 0, StoreSideFilter.All, append: true, CancellationToken.None);
        await browser.LoadAsync("", [], 0, StoreSideFilter.All, append: true, CancellationToken.None);
        await browser.LoadAsync("", [], 1, StoreSideFilter.All, append: false, CancellationToken.None);

        Assert.Equal([0, 20, 40, 0], offsets);
    }

    [Fact]
    public async Task APageComesBackWithItsHits()
    {
        var store = new FakeModrinthStore { OnSearch = (_, _, _) => Task.FromResult<SearchResponse?>(Page(3, 3)) };

        var result = await new StoreBrowser(Config(), store)
            .LoadAsync("x", [], 0, StoreSideFilter.All, append: false, CancellationToken.None);

        Assert.Equal(StoreBrowser.Kind.Page, result.Kind);
        Assert.Equal(3, result.Response!.Hits.Count);
        Assert.True(result.StillInCharge);
    }

    [Fact]
    public async Task NoAnswerAndAnErrorAreReportedNotThrown()
    {
        var offline = new StoreBrowser(Config(), new FakeModrinthStore());
        var broken = new StoreBrowser(Config(), new FakeModrinthStore
        {
            OnSearch = (_, _, _) => throw new UserFacingException("the store is down"),
        });

        var none = await offline.LoadAsync("", [], 0, StoreSideFilter.All, false, CancellationToken.None);
        var failed = await broken.LoadAsync("", [], 0, StoreSideFilter.All, false, CancellationToken.None);

        Assert.Equal(StoreBrowser.Kind.NoAnswer, none.Kind);
        Assert.Equal(StoreBrowser.Kind.Failed, failed.Kind);
        Assert.Contains("store is down", failed.Error);
    }

    // ---- two searches at once ----

    [Fact]
    public async Task ANewerSearchSupersedesTheOneStillInFlight()
    {
        var first = new TaskCompletionSource<SearchResponse?>();
        var calls = 0;
        var store = new FakeModrinthStore
        {
            OnSearch = (_, _, _) => ++calls == 1 ? first.Task : Task.FromResult<SearchResponse?>(Page(5, 5)),
        };
        var browser = new StoreBrowser(Config(), store);

        var slow = browser.LoadAsync("a", [], 0, StoreSideFilter.All, false, CancellationToken.None);
        var fast = await browser.LoadAsync("b", [], 0, StoreSideFilter.All, false, CancellationToken.None);
        first.SetResult(Page(20, 20));   // the older answer arrives last
        var stale = await slow;

        Assert.Equal(StoreBrowser.Kind.Page, fast.Kind);
        Assert.True(fast.StillInCharge);
        Assert.Equal(StoreBrowser.Kind.Superseded, stale.Kind);
        Assert.False(stale.StillInCharge);   // so it must not clear the spinner the newer one is showing
    }

    [Fact]
    public async Task CancellingPendingWorkEndsASearchInFlight()
    {
        var gate = new TaskCompletionSource<SearchResponse?>();
        var store = new FakeModrinthStore { OnSearch = (_, _, _) => gate.Task };
        var browser = new StoreBrowser(Config(), store);

        var running = browser.LoadAsync("", [], 0, StoreSideFilter.All, false, CancellationToken.None);
        browser.CancelPending();
        gate.SetResult(Page(5, 5));

        Assert.Equal(StoreBrowser.Kind.Superseded, (await running).Kind);
    }
}
