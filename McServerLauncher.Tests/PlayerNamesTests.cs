using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The one rule for what a player name looks like.
/// </summary>
public class PlayerNamesTests
{
    [Theory]
    [InlineData("Alice")]
    [InlineData("a")]
    [InlineData("Steve_2")]
    [InlineData("1234567890123456")]
    public void AValidNameIsAccepted(string name) => Assert.True(PlayerNames.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345678901234567")]
    [InlineData("two words")]
    [InlineData("a-b")]
    [InlineData("Ålice")]
    [InlineData("../etc")]
    [InlineData("Alice\n")]
    [InlineData("Alice\r\n")]
    [InlineData("\nAlice")]
    public void AnythingElseIsRefused(string? name) => Assert.False(PlayerNames.IsValid(name));

    /// <summary>
    /// A name never carries a line break, including a final one: <c>$</c> in a regex matches before
    /// it, so the five regexes this replaced let "Alice\n" through to a console command and a path.
    /// </summary>
    [Fact]
    public void AFinalLineBreakDoesNotPass()
    {
        Assert.False(PlayerNames.IsValid("Alice\n"));
        Assert.Matches("^" + PlayerNames.Pattern + "$", "Alice\n");   // what the old check said
    }

    [Fact]
    public void ThePatternIsTheSameRuleForPickingANameOutOfALine()
    {
        Assert.Matches("^(" + PlayerNames.Pattern + ") joined$", "Steve_2 joined");
        Assert.DoesNotMatch("^(" + PlayerNames.Pattern + ") joined$", "a-b joined");
    }
}
