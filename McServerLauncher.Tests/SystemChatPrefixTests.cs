using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Minecraft 26.3 logs every system message with <c>System chat: </c> in front: joins, leaves,
/// deaths, advancements and the answers to console commands. No detector found a name in front of
/// <c> joined the game</c> any more, so the player list stayed empty on every 26.3 server and the
/// idle timer stopped it with people playing (issue #167). The lines are the ones in that report.
/// </summary>
public class SystemChatPrefixTests
{
    private const string Paper = "[19:32:38 INFO]: ";
    private const string Vanilla = "[19:32:38] [Server thread/INFO]: ";
    private const string Join = " joined the game";
    private const string Leave = " left the game";

    private static readonly IReadOnlySet<string> Nobody = new HashSet<string>();

    [Theory]
    [InlineData(Paper + "System chat: JavaPlayer1 joined the game", "JavaPlayer1")]
    [InlineData(Vanilla + "System chat: JavaPlayer1 joined the game", "JavaPlayer1")]
    [InlineData(Paper + "System chat: .BedrockPlayer1 joined the game", ".BedrockPlayer1")]
    public void AJoinIsReadThroughThePrefix(string line, string name)
    {
        Assert.Equal(name, ConsoleLineClassifier.NameBefore(line, Join));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Join, name, null), PlayerEventParser.Parse(line, Nobody));
        Assert.Equal(ConsoleLineKind.Players, ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout));
    }

    [Theory]
    [InlineData(Paper + "System chat: JavaPlayer1 left the game", "JavaPlayer1")]
    [InlineData(Paper + "System chat: .BedrockPlayer1 left the game", ".BedrockPlayer1")]
    public void ALeaveIsReadThroughThePrefix(string line, string name)
    {
        Assert.Equal(name, ConsoleLineClassifier.NameBefore(line, Leave));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Leave, name, null), PlayerEventParser.Parse(line, Nobody));
    }

    /// <summary>Up to 26.2 there is no prefix, and those servers are still the most common.</summary>
    [Fact]
    public void ALineWithoutThePrefixReadsAsBefore()
    {
        Assert.Equal("JavaPlayer1", ConsoleLineClassifier.NameBefore(Paper + "JavaPlayer1 joined the game", Join));
        Assert.Equal("Saved the game", ConsoleLineClassifier.MessageBody(Paper + "Saved the game"));
    }

    [Fact]
    public void ADeathIsReadAndDoesNotCarryThePrefix()
    {
        const string line = Paper + "System chat: JavaPlayer4 was slain by Zombie";

        // The text goes into a notification as it is: "System chat: …" there would be noise.
        Assert.Equal("JavaPlayer4 was slain by Zombie", DeathMessageDetector.Detect(line));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Death, "JavaPlayer4", "JavaPlayer4 was slain by Zombie"),
            PlayerEventParser.Parse(line, Nobody));
    }

    [Fact]
    public void AnAdvancementIsRead()
    {
        Assert.Equal(new PlayerEvent(PlayerEventKind.Advancement, "JavaPlayer3", "Isn't It Iron Pick?"),
            PlayerEventParser.Parse(Paper + "System chat: JavaPlayer3 has made the advancement [Isn't It Iron Pick?]",
                Nobody));
    }

    /// <summary>
    /// The console is a system-message receiver too, so what it answers to a command has the prefix
    /// as well: the save a live backup waits for, the seed, the tick rate.
    /// </summary>
    [Fact]
    public void TheAnswersToConsoleCommandsAreRead()
    {
        Assert.True(SaveConfirmation.IsSaveFinished(Paper + "System chat: Saved the game"));
        Assert.Equal(-123L, WorldSeed.FromConsoleLine(Vanilla + "System chat: Seed: [-123]"));
        Assert.Equal(PerfLineKind.PaperTps,
            PerformanceParsers.Parse(Paper + "System chat: TPS from last 1m, 5m, 15m: 20.0, 20.0, 20.0").Kind);
        Assert.Equal(PerfLineKind.TickQueryState,
            PerformanceParsers.Parse(Vanilla + "System chat: The game is running normally").Kind);
    }

    /// <summary>An operator's command echoed to the console is not chat and not an event.</summary>
    [Fact]
    public void CommandFeedbackStaysOrdinaryOutput()
    {
        const string line = Paper + "System chat: [JavaPlayer5: Set own game mode to Survival Mode]";

        Assert.Equal(ConsoleLineKind.Info, ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout,
            online: new HashSet<string> { "JavaPlayer5" }));
        Assert.Null(PlayerEventParser.Parse(line, new HashSet<string> { "JavaPlayer5" }));
    }

    /// <summary>
    /// The prefix counts only where the server writes it. Chat is logged by another method and
    /// always starts with the sender, so typing the prefix moves nobody on or off the player list.
    /// </summary>
    [Theory]
    [InlineData(Paper + "<Bob> System chat: Alice joined the game", Join)]
    [InlineData(Paper + "<Bob> System chat: Bob left the game", Leave)]
    [InlineData(Paper + "[Not Secure] <Bob> System chat: Bob left the game", Leave)]
    [InlineData(Paper + "[Bob] System chat: Alice left the game", Leave)]
    [InlineData(Paper + "* Bob System chat: Alice left the game", Leave)]
    [InlineData(Paper + "<Bob> x]: System chat: Bob left the game", Leave)]
    [InlineData(Paper + "System chat: <Bob> Alice left the game", Leave)]
    [InlineData(Paper + "System chat: System chat: Alice left the game", Leave)]
    public void TypingThePrefixInChatIsNotAnEvent(string line, string marker)
    {
        Assert.Null(ConsoleLineClassifier.NameBefore(line, marker));
        Assert.False(SaveConfirmation.IsSaveFinished(line.Replace("Bob left the game", "Saved the game")));
    }

    [Fact]
    public void ChatThatMentionsThePrefixIsStillChat()
    {
        Assert.Equal(("Bob", "System chat: Alice joined the game"),
            ConsoleLineClassifier.ChatOf(Paper + "<Bob> System chat: Alice joined the game", null));
    }
}
