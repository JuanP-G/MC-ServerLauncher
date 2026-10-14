using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Which commands typed into the console count as changing the world, for the backups.
/// </summary>
/// <remarks>
/// A server that ran with nobody on it skips its stop backup — unless somebody changed the world
/// from the console. Looking (<c>list</c>) and talking (<c>say</c>) must not cost a copy; anything
/// that might change the world, including a plugin's command nobody here knows, must.
/// </remarks>
public class WorldCommandTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("/say hola a todos")]
    [InlineData("minecraft:list")]
    [InlineData("LIST")]
    [InlineData("whitelist add Steve")]
    [InlineData("op Alex")]
    [InlineData("time query daytime")]
    [InlineData("datapack list")]
    [InlineData("save-all flush")]
    [InlineData("   ")]
    public void LookingTalkingAndWhoMayPlayLeaveTheWorldAlone(string command) =>
        Assert.False(WorldCommand.Touches(command));

    [Theory]
    [InlineData("give Steve diamond 64")]
    [InlineData("/fill ~ ~ ~ ~10 ~10 ~10 stone")]
    [InlineData("time set day")]
    [InlineData("gamerule keepInventory true")]
    [InlineData("minecraft:weather clear")]
    [InlineData("datapack enable \"file/mine\"")]
    [InlineData("execute as @a run say hi")]
    [InlineData("chunky start")]
    public void AnythingThatMayChangeItCounts(string command) =>
        Assert.True(WorldCommand.Touches(command));
}
