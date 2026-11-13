using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Pumpkin's console, read by the same detectors that read a Java server's.
/// </summary>
/// <remarks>
/// <para>
/// Every server line here was captured from Pumpkin 0.2.2 (<c>0.2.2+26.3-26.60</c>) on Windows, run
/// through a pipe the way the app runs it, with a client joining, leaving, being kicked and being
/// inside when the server stopped. They are as the app sees them: after
/// <see cref="AnsiEscapes.Strip"/>, which is applied before anything else.
/// </para>
/// <para>
/// What is at stake is the idle shutdown. The app stops a server nobody is on, and it knows who is
/// on it from these lines alone. A join it does not recognise is a server stopped under the people
/// playing on it — which is what happened to every Minecraft 26.3 server before the
/// <c>System chat: </c> prefix was understood.
/// </para>
/// </remarks>
public class PumpkinConsoleTests
{
    private static string Read(string raw) => PumpkinConsole.Normalize(raw).Text;

    // --- joins and leaves ---

    [Theory]
    [InlineData("14:16:23  INFO Alex joined the game", "Alex")]
    [InlineData("14:15:44  INFO Steve_Test joined the game", "Steve_Test")]
    [InlineData("14:16:29  INFO Kicked_One joined the game", "Kicked_One")]
    public void AJoinIsSeen(string raw, string player)
    {
        Assert.Equal(player, ConsoleLineClassifier.NameBefore(Read(raw), " joined the game"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Join, player, null),
            PlayerEventParser.Parse(Read(raw), online: null));
    }

    [Theory]
    [InlineData("14:16:37  INFO Alex left the game", "Alex")]
    // After "kick Kicked_One", and after "stop" with Stays_In still inside: the same line both times.
    [InlineData("14:16:35  INFO Kicked_One left the game", "Kicked_One")]
    [InlineData("14:16:59  INFO Stays_In left the game", "Stays_In")]
    public void ALeaveIsSeen(string raw, string player)
    {
        Assert.Equal(player, ConsoleLineClassifier.NameBefore(Read(raw), " left the game"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Leave, player, null),
            PlayerEventParser.Parse(Read(raw), online: null));
    }

    [Fact]
    public void UnreadTheSameLinesSayNothing()
    {
        // The reason this class exists, pinned: without the rewrite the Java detectors find no
        // prefix and no name, and the player list of a Pumpkin server would stay empty for ever.
        const string raw = "14:16:23  INFO Alex joined the game";

        Assert.Null(ConsoleLineClassifier.MessageBody(raw));
        Assert.Null(ConsoleLineClassifier.NameBefore(raw, " joined the game"));
    }

    [Fact]
    public void TheLineKeepsWhatTheServerSaid()
    {
        Assert.Equal("[14:16:23 INFO]: Alex joined the game", Read("14:16:23  INFO Alex joined the game"));
        Assert.Equal("[14:16:35 WARN]: Closing connection for 1: You were kicked from the game",
            Read("14:16:35  WARN Closing connection for 1: You were kicked from the game"));
    }

    // --- what a player could type ---

    [Fact]
    public void ChatBecomesChatAndCannotFakeAnEvent()
    {
        // Pumpkin logs chat as "<chat> name: message" (chat_message.rs, Java and Bedrock alike).
        var line = Read("14:20:00  INFO <chat> Bob: Alice left the game");

        Assert.Equal("[14:20:00 INFO]: <Bob> Alice left the game", line);
        Assert.Null(ConsoleLineClassifier.NameBefore(line, " left the game"));
        Assert.Equal(("Bob", "Alice left the game"), ConsoleLineClassifier.ChatOf(line, online: null));
        Assert.Equal(ConsoleLineKind.Chat, ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout));
    }

    [Fact]
    public void ACommandAPlayerRanIsNotAnEvent()
    {
        // chat_command.rs logs every command a player runs, with whatever they typed after it.
        var line = Read("14:21:00  INFO Player (Bob): executed command /me Alice left the game");

        Assert.Null(ConsoleLineClassifier.NameBefore(line, " left the game"));
        Assert.Null(PlayerEventParser.Parse(line, online: null));
    }

    [Theory]
    [InlineData("Alex left the game")]
    [InlineData("Alex joined the game")]
    public void ABareLineIsNeverAJoinOrALeave(string bare)
    {
        // Pumpkin always logs both with its prefix. NameBefore also reads a line with no prefix at
        // all, so a bare one — the second line of anything multi-line — is put out of its reach.
        var line = PumpkinConsole.Normalize(bare);

        Assert.False(line.Recognised);
        Assert.Null(ConsoleLineClassifier.NameBefore(line.Text, " left the game"));
        Assert.Null(ConsoleLineClassifier.NameBefore(line.Text, " joined the game"));
        Assert.EndsWith(bare, line.Text);
    }

    // --- command answers, which Pumpkin prints with no prefix ---

    [Theory]
    [InlineData("There are 2 of a max of 1000 players online: Alex, Kicked_One")]
    [InlineData("There are 0 of a max of 1000 players online: ")]
    [InlineData("Kicked Kicked_One: Kicked by an operator")]
    [InlineData("Made Alex a server operator")]
    [InlineData("TPS: 20.0 MSPT: 0.17ms")]
    [InlineData("pumpkin.toml changed because values were missing. The missing values were filled with default values.")]
    public void ACommandAnswerIsLeftAsItIs(string answer)
    {
        var line = PumpkinConsole.Normalize(answer);

        Assert.Equal(answer, line.Text);
        Assert.False(line.Recognised);
    }

    [Fact]
    public void TheAnswerToSaveAllIsOneACopyOfTheWorldCanWaitFor()
    {
        // A copy taken while the server runs sends "save-all flush" and waits for this sentence.
        // Pumpkin answers it bare; read as such, every copy sat out its thirty-second timeout.
        Assert.False(SaveConfirmation.IsSaveFinished("Saved the game"));
        Assert.True(SaveConfirmation.IsSaveFinished(Read("Saved the game")));

        // The line before it is not the end of the save.
        Assert.False(SaveConfirmation.IsSaveFinished(Read("Saving the game (this may take a moment!)")));
    }

    // --- levels ---

    [Theory]
    [InlineData("14:14:38  WARN Pumpkin is currently under heavy development!", ConsoleLineKind.Warn)]
    [InlineData("14:14:38 ERROR Failed to save player data on disconnect: disk full", ConsoleLineKind.Error)]
    [InlineData("14:14:38  INFO All worlds loaded successfully.", ConsoleLineKind.Info)]
    [InlineData("14:16:23  INFO Alex joined the game", ConsoleLineKind.Players)]
    public void TheLevelSurvivesTheRewrite(string raw, ConsoleLineKind expected)
    {
        Assert.Equal(expected, ConsoleLineClassifier.Classify(Read(raw), ConsoleSource.Stdout));
    }

    // --- ready ---

    [Fact]
    public void StartedServerIsTheReadyLine()
    {
        // There is no "Done (" on Pumpkin; this is the line it logs when it accepts players.
        Assert.True(PumpkinConsole.IsReady(PumpkinConsole.Normalize("14:14:38  INFO Started server; took 168ms")));

        Assert.False(PumpkinConsole.IsReady(PumpkinConsole.Normalize(
            "14:14:38  INFO Starting Pumpkin 0.2.2+26.3-26.60 Java Minecraft (Protocol 777) | 1.26.60 Bedrock (Protocol 2225)")));
        Assert.False(PumpkinConsole.IsReady(PumpkinConsole.Normalize(
            "14:14:38  INFO Server is now running. Connect using port: Java Edition: 127.0.0.1:25599 | Bedrock Edition: 127.0.0.1:19199")));
    }

    [Fact]
    public void SayingItInChatDoesNotMakeItReady()
    {
        Assert.False(PumpkinConsole.IsReady(PumpkinConsole.Normalize(
            "14:20:00  INFO <chat> Bob: Started server; took 1ms")));
    }

    // --- a log format the app cannot follow ---

    [Theory]
    // [logging] target = true
    [InlineData("14:16:23  INFO pumpkin_core::world: Alex joined the game")]
    // [logging] threads = true
    [InlineData("14:16:23  INFO tokio-runtime-worker Alex joined the game")]
    // [logging] timestamp = false
    [InlineData(" INFO Alex joined the game")]
    // [logging] timestamp_format with a date in it
    [InlineData("2026-11-12 14:16:23  INFO Alex joined the game")]
    public void AnotherLogFormatIsNotGuessedAt(string raw)
    {
        // With a thread name or a module path in front, "Alex joined the game" cannot be told from
        // a thread called Alex. Nobody is read out of such a line, in either direction — and the
        // run is marked as one whose player list means nothing (see the test below).
        var line = PumpkinConsole.Normalize(raw).Text;

        Assert.Null(ConsoleLineClassifier.NameBefore(line, " joined the game"));
        Assert.Null(PlayerEventParser.Parse(line, online: null));
    }

    [Theory]
    [InlineData("14:14:38  INFO pumpkin: Started server; took 168ms")]
    [InlineData("14:14:38  INFO main Started server; took 168ms")]
    [InlineData(" INFO Started server; took 168ms")]
    [InlineData("2026-11-12 14:14:38  INFO Started server; took 168ms")]
    public void AnotherLogFormatStillStartsButIsNotTrusted(string raw)
    {
        // So the server does not sit on "Starting" for ever — and so the process manager knows to
        // say that this run will not be stopped for being empty.
        var line = PumpkinConsole.Normalize(raw);

        Assert.False(PumpkinConsole.IsReady(line));
        Assert.True(PumpkinConsole.LooksReady(line.Text));
    }
}
