using System.Xml.Linq;
using McServerLauncher.Localization;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// How Playit's network page judges a region from what its test host answered, and what the summary
/// line says about it. Nothing here touches the network: the answers are the ones measured by hand on
/// 2026-11-14, when Poland was being served from Stockholm.
/// </summary>
public class PlayitNetworkTests
{
    private static PlayitRegion Region(string code) => PlayitNetworkCatalog.Regions.Single(r => r.Code == code);

    private static PlayitRegionProbe Served(int dc, string name = "x-1", double ping = 20) =>
        new(true, dc, name, ping, 1);

    private static readonly PlayitRegionProbe NoAnswer = new(false, null, null, null, null);

    [Fact]
    public void ARegionServedFromItsOwnDatacenterIsOperational()
    {
        var result = PlayitNetworkCatalog.Classify(Region("pol"), Served(22, "warsaw-1"));

        Assert.Equal(PlayitRegionState.Operational, result.State);
        Assert.Null(result.ServedBy);
    }

    [Fact]
    public void PolandServedFromStockholmIsReroutedThroughSweden()
    {
        var result = PlayitNetworkCatalog.Classify(Region("pol"), Served(11, "stockholm-2"));

        Assert.Equal(PlayitRegionState.Rerouted, result.State);
        Assert.Equal("sweden", result.ServedBy?.Slug);
    }

    [Fact]
    public void TheGlobalAddressIsFineWhereverItLands() =>
        Assert.Equal(PlayitRegionState.Operational,
            PlayitNetworkCatalog.Classify(Region("gl"), Served(19, "sydney-2")).State);

    [Fact]
    public void AContinentServedByAnyOfItsDatacentersIsOperational()
    {
        Assert.Equal(PlayitRegionState.Operational, PlayitNetworkCatalog.Classify(Region("eu"), Served(37)).State);
        Assert.Equal(PlayitRegionState.Operational, PlayitNetworkCatalog.Classify(Region("eu"), Served(36)).State);
        Assert.Equal(PlayitRegionState.Operational, PlayitNetworkCatalog.Classify(Region("na"), Served(45)).State);
    }

    [Fact]
    public void AContinentServedFromAnotherContinentIsRerouted()
    {
        var result = PlayitNetworkCatalog.Classify(Region("eu"), Served(45, "miami-3"));

        Assert.Equal(PlayitRegionState.Rerouted, result.State);
        Assert.Equal("miami", result.ServedBy?.Slug);   // the city, not the continent: it says more
    }

    [Fact]
    public void MiamiServedByItsDatacenterIsNotARerouteThroughNorthAmerica() =>
        // Miami's datacenter is also North America's: it must count as Miami's own.
        Assert.Equal(PlayitRegionState.Operational,
            PlayitNetworkCatalog.Classify(Region("mia"), Served(45, "miami-3")).State);

    [Fact]
    public void ADatacenterTheTableDoesNotKnowIsNeverCalledAReroute() =>
        Assert.Equal(PlayitRegionState.Operational,
            PlayitNetworkCatalog.Classify(Region("pol"), Served(999, "somewhere-new-1")).State);

    [Fact]
    public void NoAnswerIsDown() =>
        Assert.Equal(PlayitRegionState.Down, PlayitNetworkCatalog.Classify(Region("deu"), NoAnswer).State);

    [Fact]
    public void ARegionPlayitListsOfflineIsDownEvenIfItsHostAnswers() =>
        Assert.Equal(PlayitRegionState.Down,
            PlayitNetworkCatalog.Classify(Region("deu"), Served(36), listedOffline: true).State);

    [Fact]
    public void ARegionIsListedOfflineOnlyWhenNoneOfItsDatacentersIsOnline()
    {
        var offline = PlayitNetworkCatalog.OfflineSlugs(
        [
            ("india", true), ("india", false), ("india", false),
            ("poland", false),
            ("europe", true),
        ]);

        Assert.Equal(["poland"], offline);
    }

    [Fact]
    public void TheNarrowestRegionOwnsADatacenter()
    {
        Assert.Equal("sweden", PlayitNetworkCatalog.HomeOf(11)?.Slug);   // not Europe
        Assert.Equal("miami", PlayitNetworkCatalog.HomeOf(45)?.Slug);    // not North America
        Assert.Equal("europe", PlayitNetworkCatalog.HomeOf(37)?.Slug);   // Madrid is only Europe's
        Assert.Null(PlayitNetworkCatalog.HomeOf(999));
    }

    [Fact]
    public void TheCatalogMatchesPlayitsPingPage()
    {
        var regions = PlayitNetworkCatalog.Regions;

        Assert.Equal(22, regions.Count);
        Assert.Equal(regions.Count, regions.Select(r => r.Code).Distinct().Count());
        Assert.Equal(regions.Count, regions.Select(r => r.Slug).Distinct().Count());
        Assert.Equal("gl", regions[0].Code);
        Assert.Single(regions, r => r.AcceptsAny);
    }

    [Fact]
    public void EveryRegionHasANameInEveryLanguage()
    {
        foreach (var file in new[] { "Strings.resx", "Strings.en.resx", "Strings.de.resx", "Strings.fr.resx", "Strings.pt.resx" })
        {
            var keys = XDocument.Load(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Resources", file))
                .Root!.Elements("data").Select(d => d.Attribute("name")?.Value).ToHashSet();
            foreach (var region in PlayitNetworkCatalog.Regions)
                Assert.True(keys.Contains(region.NameKey), $"{file}: falta {region.NameKey}");
        }
    }

    [Theory]
    [InlineData("madrid-2", "Madrid")]
    [InlineData("stockholm-2", "Stockholm")]
    [InlineData("sao-paulo-1", "Sao Paulo")]
    [InlineData("FremontCa", "FremontCa")]
    [InlineData("", "")]
    public void ADatacenterNameReadsAsItsCity(string name, string city) =>
        Assert.Equal(city, PlayitNetworkCatalog.CityOf(name));

    [Fact]
    public void PingIsTheMedianAndJitterTheMeanChange()
    {
        var (ping, jitter) = PlayitNetworkCatalog.Stats([10, 12, 40, 11]);

        Assert.Equal(11.5, ping);                 // the slow 40 does not drag it up
        Assert.Equal((2 + 28 + 29) / 3.0, jitter);
    }

    [Fact]
    public void ASingleSampleHasNoJitter() => Assert.Equal((7.0, 0.0), PlayitNetworkCatalog.Stats([7]));

    [Fact]
    public void TheTestHostsAnswerIsRead()
    {
        Assert.Equal((37, "madrid-2"),
            PlayitNetworkCatalog.ParseDataCenter("""{"dc":"madrid-2","dc_id":37,"region":"europe"}"""));
        Assert.Equal(((int?)null, (string?)null), PlayitNetworkCatalog.ParseDataCenter("not json"));
    }

    [Fact]
    public void WhereThisMachineIsRoutedIsRead()
    {
        Assert.Equal("europe", PlayitNetworkCatalog.ParseYourRegion(
            """{"status":"success","data":{"region":"europe","pop":"Spain"}}"""));
        Assert.Null(PlayitNetworkCatalog.ParseYourRegion("""{"status":"error","data":{"region":"europe"}}"""));
        Assert.Null(PlayitNetworkCatalog.ParseYourRegion("<html>"));
    }

    [Fact]
    public void PlayitsListOfDatacentersIsRead()
    {
        var pops = PlayitNetworkCatalog.ParsePops("""
            {"status":"success","data":{"pops":[
              {"pop":"Poland","name":"Poland","region":"poland","online":false,"ip4_premium":false},
              {"pop":"Spain","name":"Spain","region":"europe","online":true,"ip4_premium":false}],
             "regions":["global"]}}
            """);

        Assert.Equal([("poland", false), ("europe", true)], pops);
    }

    // ---------------------------------------------------------------- the summary line

    private static PlayitRegionResult Ok(string code) =>
        PlayitNetworkCatalog.Classify(Region(code), Served(Region(code).HomeDataCenters.FirstOrDefault(37)));

    [Fact]
    public void WithEverythingWorkingTheLineSaysSoAndWhereYouEnter()
    {
        var (text, level) = PlayitNetworkViewModel.Describe(
            [Ok("gl"), Ok("eu"), Ok("deu")], yourCity: "Madrid", yourPing: 4, measuring: false);

        Assert.Equal(PlayitNetworkLevel.Ok, level);
        Assert.StartsWith(Localizer.Get("PlayNet_AllOk"), text);
        Assert.Contains("Madrid", text);
    }

    [Fact]
    public void OneRerouteIsNamedWithWhereItGoes()
    {
        var poland = PlayitNetworkCatalog.Classify(Region("pol"), Served(11, "stockholm-2"));

        var (text, level) = PlayitNetworkViewModel.Describe([Ok("gl"), poland], "Madrid", 4, measuring: false);

        Assert.Equal(PlayitNetworkLevel.Warning, level);
        Assert.StartsWith(Localizer.Format("PlayNet_OneReroutedFmt",
            Localizer.Get("PlayNet_R_poland"), Localizer.Get("PlayNet_R_sweden")), text);
    }

    [Fact]
    public void SeveralProblemsAreCountedAndTheFirstNamed()
    {
        var down = new[] { "deu", "gbr", "swe", "rou" }
            .Select(c => PlayitNetworkCatalog.Classify(Region(c), NoAnswer)).ToList();

        var (text, level) = PlayitNetworkViewModel.Describe([Ok("gl"), .. down], null, null, measuring: false);

        Assert.Equal(PlayitNetworkLevel.Warning, level);
        Assert.Contains("4", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void TheGlobalAddressDownIsRed()
    {
        var gl = PlayitNetworkCatalog.Classify(Region("gl"), NoAnswer);

        Assert.Equal(PlayitNetworkLevel.Bad, PlayitNetworkViewModel.Describe([gl, Ok("eu")], null, null, false).Level);
    }

    [Fact]
    public void NothingAnsweringIsAProblemWithThisMachinesConnection()
    {
        var all = PlayitNetworkCatalog.Regions.Select(r => PlayitNetworkCatalog.Classify(r, NoAnswer)).ToList();

        var (text, level) = PlayitNetworkViewModel.Describe(all, null, null, measuring: false);

        Assert.Equal(PlayitNetworkLevel.Bad, level);
        Assert.Equal(Localizer.Get("PlayNet_Unreachable"), text);
    }

    [Fact]
    public void BeforeAnyResultTheLineSaysItIsMeasuring() =>
        Assert.Equal(Localizer.Get("PlayNet_Measuring"),
            PlayitNetworkViewModel.Describe([], null, null, measuring: true).Text);
}
