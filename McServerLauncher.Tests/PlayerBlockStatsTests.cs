using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading the blocks a player has broken and the items they have used, out of the server's own
/// statistics file.
/// </summary>
/// <remarks>
/// <para>
/// The file is written by the server while this reads it, and its shape changes between versions,
/// so nothing here may throw: a category that is missing, truncated or not the shape it should be
/// gives an empty list, which the profile simply does not draw.
/// </para>
/// <para>
/// There is no "blocks placed" counter in Minecraft. <c>minecraft:used</c> is "times you used this
/// item", which for a block means placing it and for a shovel means a swing — shown as it is, with
/// a line saying so, rather than filtered against a list of block ids that would go stale.
/// </para>
/// </remarks>
public class PlayerBlockStatsTests
{
    private const string Full = """
        {
          "stats": {
            "minecraft:mined": {
              "minecraft:stone": 800,
              "minecraft:dirt": 150,
              "minecraft:deepslate_diamond_ore": 40,
              "minecraft:ancient_debris": 10
            },
            "minecraft:used": { "minecraft:torch": 300, "minecraft:cobblestone": 100 },
            "minecraft:crafted": { "minecraft:stick": 64 },
            "minecraft:killed": { "minecraft:zombie": 41, "minecraft:creeper": 12 },
            "minecraft:killed_by": { "minecraft:creeper": 3 },
            "minecraft:custom": {
              "minecraft:play_time": 72000,
              "minecraft:deaths": 3,
              "minecraft:jump": 5000,
              "minecraft:damage_dealt": 1234,
              "minecraft:damage_taken": 567,
              "minecraft:walk_one_cm": 200000,
              "minecraft:sprint_one_cm": 300000,
              "minecraft:fly_one_cm": 50000,
              "minecraft:aviate_one_cm": 900000,
              "minecraft:fall_one_cm": 40000
            }
          },
          "DataVersion": 4700
        }
        """;

    [Fact]
    public void TheBlocksBrokenComeBackBiggestFirst()
    {
        var stats = PlayerStatsReader.Parse(Full)!;

        Assert.Equal(
            new[] { "minecraft:stone", "minecraft:dirt", "minecraft:deepslate_diamond_ore", "minecraft:ancient_debris" },
            stats.Mined.Select(e => e.Id));
        Assert.Equal(800, stats.Mined[0].Count);
        Assert.Equal(1000, stats.BlocksMined);
    }

    [Fact]
    public void OresAreCountedApartFromTheRestOfTheStone()
    {
        // Including ancient debris, which is what netherite comes out of and is not called an ore.
        Assert.Equal(50, PlayerStatsReader.Parse(Full)!.OresMined);
    }

    [Fact]
    public void TheOtherCategoriesAreReadToo()
    {
        var stats = PlayerStatsReader.Parse(Full)!;

        Assert.Equal("minecraft:torch", stats.Used[0].Id);
        Assert.Equal("minecraft:stick", stats.Crafted[0].Id);
        Assert.Equal("minecraft:zombie", stats.Killed[0].Id);
        Assert.Equal("minecraft:creeper", stats.KilledBy[0].Id);
    }

    [Fact]
    public void TheOddsAndEndsAreReadToo()
    {
        var stats = PlayerStatsReader.Parse(Full)!;

        Assert.Equal(5000, stats.Jumps);
        Assert.Equal(1234, stats.DamageDealt);
        Assert.Equal(567, stats.DamageTaken);
        Assert.Equal(500_000, stats.WalkedCm);      // walking and sprinting
        Assert.Equal(50_000, stats.FlownCm);
        Assert.Equal(900_000, stats.ElytraCm);

        // Falling is not travelling, and it is the one distance left out of the total.
        Assert.Equal(200_000 + 300_000 + 50_000 + 900_000, stats.DistanceCm);
    }

    [Theory]
    [InlineData("""{"stats":{"minecraft:custom":{"minecraft:deaths":7}}}""")]
    [InlineData("""{"stats":{"minecraft:mined":"esto no es un objeto"}}""")]
    [InlineData("""{"stats":{"minecraft:mined":{"minecraft:stone":"ochocientos"}}}""")]
    public void AMissingOrWrongCategoryIsAnEmptyListAndNotACrash(string json)
    {
        var stats = PlayerStatsReader.Parse(json);

        Assert.NotNull(stats);
        Assert.Empty(stats!.Mined);
        Assert.Equal(0, stats.BlocksMined);
    }

    [Fact]
    public void ARottenFileIsNoStatisticsAtAll()
    {
        Assert.Null(PlayerStatsReader.Parse("{ esto no es json"));
        Assert.Null(PlayerStatsReader.Parse(""));
        Assert.Null(PlayerStatsReader.Parse(null));
        Assert.Null(PlayerStatsReader.Parse("""{"DataVersion":4700}"""));
    }

    [Fact]
    public void ALongListIsCutBeforeItBecomesObjects()
    {
        // A player who has been at it for years touches hundreds of block types and nobody reads
        // past the first few; cutting in the reader means the rest is never built at all.
        var many = string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"minecraft:b{i}\":{1000 - i}"));
        var stats = PlayerStatsReader.Parse("{\"stats\":{\"minecraft:mined\":{" + many + "}}}")!;

        Assert.Equal(200, stats.Mined.Count);
        Assert.Equal("minecraft:b0", stats.Mined[0].Id);     // the biggest survived the cut
    }

    // --- The share each one has of its list ---

    [Fact]
    public void EachRowKnowsItsShareOfTheTotal()
    {
        var stats = PlayerStatsReader.Parse(Full)!;
        var rows = stats.Mined.Select(e => new StatBarViewModel(e, stats.BlocksMined)).ToList();

        Assert.Equal(80, rows[0].Percent, 3);
        Assert.Equal(15, rows[1].Percent, 3);
        Assert.Equal(100, rows.Sum(r => r.Percent), 3);
    }

    [Fact]
    public void AnEmptyListDoesNotDivideByZero()
    {
        var row = new StatBarViewModel(new StatEntry("minecraft:stone", 5), total: 0);

        Assert.Equal(0, row.Percent);
    }

    // --- Names people can read ---

    [Theory]
    [InlineData("minecraft:deepslate_diamond_ore", "Deepslate diamond ore")]
    [InlineData("minecraft:stone", "Stone")]
    [InlineData("minecraft:ancient_debris", "Ancient debris")]
    // A block from a mod keeps its namespace: "Source gem" on its own is a mystery.
    [InlineData("ars_nouveau:source_gem", "ars_nouveau: Source gem")]
    [InlineData("stone", "Stone")]
    [InlineData("", "")]
    public void AnIdBecomesSomethingReadable(string id, string expected) =>
        Assert.Equal(expected, MinecraftIds.Pretty(id));

    [Theory]
    [InlineData("minecraft:iron_ore", true)]
    [InlineData("minecraft:deepslate_gold_ore", true)]
    [InlineData("minecraft:nether_quartz_ore", true)]
    [InlineData("minecraft:ancient_debris", true)]
    [InlineData("minecraft:stone", false)]
    [InlineData("minecraft:ore_block_of_lies", false)]
    public void AnOreIsSpottedByItsName(string id, bool expected) =>
        Assert.Equal(expected, MinecraftIds.LooksLikeOre(id));
}
