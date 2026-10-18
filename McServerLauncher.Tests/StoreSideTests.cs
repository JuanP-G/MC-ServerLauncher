using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Where a mod runs, and which mods the store lists because of it.
/// </summary>
/// <remarks>
/// <para>
/// Written from a Fabric 26.2 server whose owner searched for Sodium, MaLiLib and LambDynamicLights
/// and found none of them. The search always added <c>server_side:required|optional</c>, which hid
/// every mod that says its server side is "unsupported": about 4,400 of the 13,100 for that version.
/// They are client-only mods, which is exactly what a player's pack is made of, and the store gave
/// no sign they existed.
/// </para>
/// <para>
/// The projects are real, with the sides Modrinth returned for them. Nothing here touches the
/// network: the search is checked by the facets it would send.
/// </para>
/// </remarks>
public class StoreSideTests
{
    private static StoreItem Item(string clientSide, string serverSide, params string[] environment) => new()
    {
        Id = "x",
        Title = "X",
        ClientSide = clientSide,
        ServerSide = serverSide,
        Environment = environment
    };

    // --- Where a project runs ---

    [Theory]
    [InlineData("required", "unsupported")]   // Sodium, MaLiLib, LambDynamicLights
    [InlineData("optional", "unsupported")]   // a cosmetic mod that works without anything on the server
    public void AServerThatCannotUseItMakesItClientOnly(string client, string server) =>
        Assert.Equal(InstallSide.ClientOnly, Item(client, server).Side);

    [Fact]
    public void AClientThatCannotUseItMakesItServerOnly() =>
        Assert.Equal(InstallSide.ServerOnly, Item("unsupported", "required").Side);

    [Theory]
    [InlineData("required", "required")]
    [InlineData("optional", "required")]
    [InlineData("required", "optional")]
    public void AnythingBothSidesUseRunsOnBoth(string client, string server) =>
        Assert.Equal(InstallSide.Both, Item(client, server).Side);

    [Theory]
    [InlineData("unknown", "unknown")]
    [InlineData("required", "unknown")]
    [InlineData("unknown", "required")]
    public void HalfAnAnswerIsNoAnswer(string client, string server) =>
        Assert.Equal(InstallSide.Unknown, Item(client, server).Side);

    [Fact]
    public void WithoutTheDeprecatedPairTheEnvironmentWordDecides()
    {
        // client_side and server_side are deprecated in the API. If they stop being sent, the
        // single word that replaced them has to be enough.
        Assert.Equal(InstallSide.ClientOnly, Item(null!, null!, "client_only").Side);
        Assert.Equal(InstallSide.ServerOnly, Item(null!, null!, "server_only").Side);
        Assert.Equal(InstallSide.Both, Item(null!, null!, "client_and_server").Side);
        Assert.Equal(InstallSide.Unknown, Item(null!, null!, "unknown").Side);
        Assert.Equal(InstallSide.Unknown, Item(null!, null!).Side);
    }

    [Fact]
    public void AClientOnlyModIsNotOneThePlayersAlsoNeed()
    {
        // The warning "players must install it too" is about a mod that does something on the
        // server as well. A client-only mod has its own badge and its own destination.
        Assert.False(Item("required", "unsupported").NeedsClient);
        Assert.True(Item("required", "required").NeedsClient);
    }

    // --- What the sentence under the name says ---

    [Fact]
    public void TheSentenceNamesWhereAClientOnlyModGoes() =>
        Assert.Equal(Localizer.Get("Store_Side_ClientOnly"),
            StoreSummaryService.SideSentence(Item("required", "unsupported")));

    [Fact]
    public void NoAnswerIsNotPresentedAsOptional()
    {
        // The old sentence for "we do not know" was "on the client it is optional".
        Assert.Equal(Localizer.Get("Store_Side_Unknown"),
            StoreSummaryService.SideSentence(Item("unknown", "unknown")));
        Assert.Equal(Localizer.Get("Store_Side_ClientOptional"),
            StoreSummaryService.SideSentence(Item("optional", "required")));
    }

    [Fact]
    public void AClientOnlyModIsNotSaidToBeForTheServer()
    {
        // The generic card line used to be "a mod for your server", under a Sodium that does
        // nothing there.
        var sodium = new StoreItem { Id = "s", Title = "Sodium", ClientSide = "required", ServerSide = "unsupported" };
        var summaries = new StoreSummaryService(Path.Combine(Path.GetTempPath(),
            "no-such-summaries-" + Guid.NewGuid() + ".json"));

        var blurb = summaries.Blurb(sodium, Array.Empty<McServerLauncher.Models.Store.StoreTagDefinition>());

        Assert.Equal(Localizer.Get("Store_Fallback_ClientMod"), blurb.Summary);
        Assert.NotEqual(Localizer.Get("Store_Fallback_Mod"), blurb.Summary);
    }

    // --- Which mods the browser lists ---

    [Fact]
    public void EverythingIsListedByDefault()
    {
        var groups = ModrinthService.FacetGroups(ServerType.Fabric, "26.2", StoreSideFilter.All);

        Assert.DoesNotContain(groups, g => g.Contains("server_side") || g.Contains("environment"));
        Assert.Contains("[\"versions:26.2\"]", groups);
        Assert.Contains("[\"categories:fabric\"]", groups);
        Assert.Contains("[\"project_type:mod\"]", groups);
    }

    [Fact]
    public void TheServerChoiceLeavesOutOnlyTheClientOnlyWords()
    {
        // Excluding the two words, rather than listing the other eight, is what keeps a project
        // whose environment is unknown in the results.
        var groups = ModrinthService.FacetGroups(ServerType.Fabric, "26.2", StoreSideFilter.ServerSide);

        Assert.Contains("[\"environment!=client_only\"]", groups);
        Assert.Contains("[\"environment!=singleplayer_only\"]", groups);
        Assert.DoesNotContain(groups, g => g.Contains("server_side"));
    }

    [Fact]
    public void TheClientOnlyChoiceListsJustThose()
    {
        var groups = ModrinthService.FacetGroups(ServerType.Fabric, "26.2", StoreSideFilter.ClientOnly);

        Assert.Contains(groups, g => g.Contains("environment:client_only")
                                     && g.Contains("environment:singleplayer_only"));
    }

    [Fact]
    public void APluginServerIsNeverOfferedAClientOnlyMod()
    {
        // There is no client to put a plugin on, whatever was asked for.
        foreach (var side in new[] { StoreSideFilter.All, StoreSideFilter.ClientOnly })
        {
            var groups = ModrinthService.FacetGroups(ServerType.Paper, "1.21.4", side);

            Assert.Contains("[\"environment!=client_only\"]", groups);
            Assert.DoesNotContain(groups, g => g.Contains("environment:client_only"));
        }
    }

    [Fact]
    public void CategoryChipsStillStackOnTopOfTheSide()
    {
        var groups = ModrinthService.FacetGroups(ServerType.Fabric, "26.2", StoreSideFilter.ClientOnly,
            new[] { "[\"categories:optimization\"]" });

        Assert.Contains("[\"categories:optimization\"]", groups);
    }
}
