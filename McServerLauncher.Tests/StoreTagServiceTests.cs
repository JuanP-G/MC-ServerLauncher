using McServerLauncher.Models.Store;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What the store says a mod is about, checked against mods that exist.
/// </summary>
/// <remarks>
/// <para>
/// Written from what a user saw: the browser tagged Travelers Backpack, Farmer's Delight and a
/// compass that finds biomes as <em>Combat</em>, and each card then opened with "Mod de combate para
/// tu servidor". Forty of the 300 most downloaded Fabric mods for 26.2 carried that tag; about eight
/// are about combat. Two things did it. The Modrinth category <c>equipment</c> (armour, tools,
/// backpacks, anything that is an item) was mapped to Combat, and the keywords were substrings, so
/// <c>sword</c> was found inside "password", <c>tps</c> inside "https", <c>quest</c> inside
/// "request" and <c>warp</c> inside "warped".
/// </para>
/// <para>
/// The projects below are real, with the categories and the one-line description Modrinth returned
/// for them, so these tests keep saying something about the store as it is and not about a
/// tidied-up version of it. Nothing here touches the network.
/// </para>
/// </remarks>
public class StoreTagServiceTests
{
    /// <summary>The shipped catalogue, read without looking at the developer's own override.</summary>
    private static readonly StoreTagService Tags = new(Path.Combine(Path.GetTempPath(),
        "no-such-store-tags-" + Guid.NewGuid() + ".json"));

    private static StoreItem Item(string title, string description, string projectType = "mod",
        string? clientSide = null, string? serverSide = null, params string[] categories) => new()
    {
        Id = title.ToLowerInvariant().Replace(' ', '-'),
        Slug = title.ToLowerInvariant().Replace(' ', '-'),
        Title = title,
        Description = description,
        ProjectType = projectType,
        ClientSide = clientSide,
        ServerSide = serverSide,
        Categories = categories
    };

    private static string[] Topics(StoreItem item, int max = int.MaxValue) =>
        Tags.Classify(item, max, StoreTagService.TopicKind).Select(t => t.Id).ToArray();

    // --- The words ---------------------------------------------------------------------------

    [Theory]
    [InlineData("A tool that downloads over https://example.com", "performance")]       // tps
    [InlineData("Use feature flags to turn parts off", "performance")]                  // lag
    [InlineData("Sends a request to the web", "adventure")]                              // quest
    [InlineData("Draws the warped forest differently", "transportation")]               // warp
    [InlineData("Lets villagers wear hats", "structures")]                               // village
    [InlineData("A prank mod for your friends", "permissions")]                          // rank
    [InlineData("Keeps the urban area clean", "moderation")]                             // ban
    [InlineData("Stores your password safely", "combat")]                                // sword
    public void AKeywordInsideAnotherWordIsNotTheKeyword(string description, string tag)
    {
        var item = Item("Something", description);

        Assert.DoesNotContain(tag, Topics(item));
    }

    [Fact]
    public void ARealKeywordStillCounts()
    {
        var item = Item("Boost", "Runs the server faster and reduces tps drops");

        Assert.Contains("performance", Topics(item));
    }

    [Fact]
    public void AStarTakesAnyEnding()
    {
        // "optimiz*" is meant to cover optimize, optimizes, optimization and optimizing.
        Assert.Contains("performance", Topics(Item("X", "Optimization for the renderer")));
        Assert.Contains("performance", Topics(Item("X", "It optimizes chunk loading")));
    }

    [Fact]
    public void PunctuationSeparatesWordsAndAccentsDoNotMatter()
    {
        Assert.Equal(" https example com ", StoreTagService.Words("https://example.com"));
        Assert.Equal(" generacion de mundo ", StoreTagService.Words("Generación de MUNDO!"));
    }

    // --- The categories ----------------------------------------------------------------------

    [Fact]
    public void EquipmentIsNotCombat()
    {
        // The case that started it: a backpack, filed by its author under equipment and storage.
        var item = Item("Travelers Backpack",
            "Unique and upgradeable backpacks with customisation, Curios API/Trinkets support",
            categories: new[] { "adventure", "decoration", "equipment", "fabric", "forge", "management",
                "neoforge", "storage" });

        var topics = Topics(item);

        Assert.DoesNotContain("combat", topics);
        Assert.Contains("storage", topics);
    }

    [Fact]
    public void ACompassThatFindsBiomesIsAboutBiomes()
    {
        // Its author ticked six of the topics, so none of them can be trusted by itself; the
        // description is what decides.
        var item = Item("Natures Compass", "Allows you to locate biomes anywhere in the world.",
            categories: new[] { "adventure", "equipment", "fabric", "forge", "game-mechanics", "neoforge",
                "technology", "utility", "worldgen" });

        Assert.Equal(new[] { "biomes" }, Topics(item));
    }

    [Fact]
    public void ACombatModIsStillCombat()
    {
        var melee = Item("Better Combat", "Easy, spectacular and fun melee combat system for Minecraft",
            categories: new[] { "adventure", "equipment", "fabric", "forge", "game-mechanics", "library",
                "neoforge" });
        var archery = Item("Archers", "Draw, Release, Conquer - Master the art of Archery",
            categories: new[] { "adventure", "equipment", "fabric", "forge", "game-mechanics", "magic",
                "neoforge", "worldgen" });

        Assert.Contains("combat", Topics(melee));
        Assert.Contains("combat", Topics(archery));
    }

    [Fact]
    public void ALibraryIsALibraryAndAtMostOneThingMore()
    {
        // GeckoLib mentions armour in its description and lists utility, quality of life and
        // library; the owner needs to know it is a library, and that is the first thing said.
        var item = Item("GeckoLib", "A 3D animation library for entities, blocks, items, armor, and more!",
            categories: new[] { "fabric", "forge", "game-mechanics", "library", "neoforge", "quilt",
                "utility" });

        var topics = Topics(item);

        Assert.Equal("library", topics[0]);
        Assert.True(topics.Length <= 2, string.Join(",", topics));
        Assert.DoesNotContain("combat", topics);
    }

    [Fact]
    public void UtilityOnlyAppearsWhenNothingBetterDoes()
    {
        var onlyUtility = Item("ModMenu", "Adds a mod menu to view the list of mods you have installed.",
            categories: new[] { "fabric", "utility" });
        var withTopic = Item("Sodium", "A rendering engine replacement for fps",
            categories: new[] { "fabric", "optimization", "utility" });

        Assert.Equal(new[] { "utility" }, Topics(onlyUtility));
        Assert.DoesNotContain("utility", Topics(withTopic));
        Assert.Contains("performance", Topics(withTopic));
    }

    [Fact]
    public void WithNothingToGoOnThereAreNoTags()
    {
        var item = Item("Whatever", "A thing.", categories: new[] { "fabric" });

        Assert.Empty(Tags.Classify(item));
    }

    // --- Mods and plugins --------------------------------------------------------------------

    [Fact]
    public void ATagForPluginsIsNotGivenToAMod()
    {
        const string description = "Adds ranks and a chat prefix for every group";

        Assert.DoesNotContain("permissions", Topics(Item("Ranks", description, "mod")));
        Assert.Contains("permissions", Topics(Item("Ranks", description, "plugin")));
    }

    [Fact]
    public void AnItemThatDoesNotSayWhatItIsIsNotRestricted()
    {
        Assert.Contains("permissions",
            Topics(Item("Ranks", "Adds ranks and a chat prefix for every group", projectType: "")));
    }

    // --- How it has to be installed ----------------------------------------------------------

    [Fact]
    public void TheInstallSideIsAFactNotAGuess()
    {
        var needsClient = Item("Mod", "Does something", clientSide: "required", serverSide: "required");
        var serverOnly = Item("Mod", "Does something", clientSide: "unsupported", serverSide: "required");

        Assert.Contains("needs-client", Tags.Classify(needsClient).Select(t => t.Id));
        Assert.Contains("server-only", Tags.Classify(serverOnly).Select(t => t.Id));
        Assert.Empty(Tags.Classify(needsClient, onlyKind: StoreTagService.TopicKind));
    }

    // --- The catalogue itself ----------------------------------------------------------------

    [Fact]
    public void EveryTagIsNamedInEveryLanguage()
    {
        foreach (var tag in Tags.All)
            foreach (var lang in new[] { "es", "en", "de", "fr", "pt" })
                Assert.True(tag.Labels.TryGetValue(lang, out var label) && !string.IsNullOrWhiteSpace(label),
                    $"tag '{tag.Id}' has no {lang} name");
    }

    [Fact]
    public void TagIdsAreUniqueAndEveryChipCanFilterSomething()
    {
        Assert.Equal(Tags.All.Count, Tags.All.Select(t => t.Id).Distinct().Count());

        foreach (var chip in Tags.All.Where(t => t.Browse))
            Assert.True(chip.Facets.Count > 0 || !string.IsNullOrWhiteSpace(chip.Query),
                $"chip '{chip.Id}' filters nothing");
    }

    [Fact]
    public void NoKeywordIsEmptyAndNoneIsAFragmentOfAnotherWord()
    {
        // A keyword left over from the substring days ("lag ", "ban ") would still compile, but
        // would never match: the trailing space is gone once the text is split into words.
        foreach (var tag in Tags.All)
            foreach (var keyword in tag.Keywords)
            {
                Assert.False(string.IsNullOrWhiteSpace(keyword), $"empty keyword in '{tag.Id}'");
                Assert.Equal(keyword.Trim(), keyword);
            }
    }

    // --- What a card says --------------------------------------------------------------------

    [Fact]
    public void AProjectWithNoTagShowsWhatItsAuthorWroteInsteadOfAGuess()
    {
        var item = Item("Stairsit", "Simple stair sitting plugin.", "plugin", categories: new[] { "paper" });

        var blurb = new StoreSummaryService(Path.Combine(Path.GetTempPath(),
            "no-such-summaries-" + Guid.NewGuid() + ".json")).Blurb(item, Array.Empty<StoreTagDefinition>());

        Assert.Equal("Simple stair sitting plugin.", blurb.Summary);
        Assert.Equal(string.Empty, blurb.Tagline);
    }
}
