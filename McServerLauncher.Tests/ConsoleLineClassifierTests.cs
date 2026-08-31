using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading what a console line is about, so it can be coloured and filtered.
/// </summary>
/// <remarks>
/// Every line in these tests is a real log entry shape, not an invented one. The console had no test
/// of any kind before this, and a classifier that is wrong is worse than none: a warning painted
/// grey is a warning nobody sees, and an ordinary line painted red is a scare with no cause.
/// </remarks>
public class ConsoleLineClassifierTests
{
    private static ConsoleLineKind Of(string line) =>
        ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout);

    // --- Severity from the log prefix ---

    [Fact]
    public void VanillaPutsTheLevelInTheSecondBracket()
    {
        // The shape that would be missed by anything anchored to the start of the line: the first
        // bracket is the time, and the level is in the one after it.
        Assert.Equal(ConsoleLineKind.Warn,
            Of("[12:34:56] [Server thread/WARN]: Can't keep up! Is the server overloaded?"));

        Assert.Equal(ConsoleLineKind.Error,
            Of("[12:34:56] [Server thread/ERROR]: Encountered an unexpected exception"));

        Assert.Equal(ConsoleLineKind.Info,
            Of("[12:34:56] [Server thread/INFO]: Preparing level \"world\""));
    }

    [Fact]
    public void PaperPutsItInTheFirstOne()
    {
        // Paper and its descendants write [HH:mm:ss LEVEL]: instead. Reading only vanilla's shape
        // would leave every Paper warning grey — and Paper is the type most people run.
        Assert.Equal(ConsoleLineKind.Warn, Of("[12:34:56 WARN]: Legacy plugin detected"));
        Assert.Equal(ConsoleLineKind.Error, Of("[12:34:56 ERROR]: Could not load 'plugins/Foo.jar'"));
        Assert.Equal(ConsoleLineKind.Info, Of("[12:34:56 INFO]: Done (17.000s)! For help, type \"help\""));
    }

    [Fact]
    public void TheOtherWordsForBadAreUnderstood()
    {
        Assert.Equal(ConsoleLineKind.Error, Of("[12:34:56] [main/SEVERE]: no"));
        Assert.Equal(ConsoleLineKind.Error, Of("[12:34:56] [main/FATAL]: no"));
        Assert.Equal(ConsoleLineKind.Warn, Of("[12:34:56] [main/WARNING]: careful"));
    }

    [Fact]
    public void AModLoaderLineWithThreeBracketsStillWorks()
    {
        // NeoForge writes [time] [thread/LEVEL] [modid]: message.
        Assert.Equal(ConsoleLineKind.Error,
            Of("[12:34:56] [main/ERROR] [net.minecraftforge.fml]: Failed to load mod"));
    }

    [Fact]
    public void TheLevelIsOnlyReadFromThePrefix()
    {
        // A player pasting a whole log line into chat is the case that matters, and the only one
        // that actually reaches the level search: "[ERROR]" on its own does not look like a prefix
        // to it, but a full "[12:00:00 ERROR]" does. Without the search being bounded to the real
        // prefix, anybody on the server could paint their own chat red — or, worse, make an
        // ordinary message look like the server reporting a failure.
        Assert.Equal(ConsoleLineKind.Chat,
            Of("[12:34:56] [Server thread/INFO]: <Alice> mirad esto: [12:00:00 ERROR]: se rompio todo"));

        Assert.Equal(ConsoleLineKind.Chat, Of("[12:34:56] [Server thread/INFO]: <Alice> [ERROR] mira esto"));
        Assert.Equal(ConsoleLineKind.Info, Of("[12:34:56] [Server thread/INFO]: loaded ERROR_HANDLER config"));
    }

    [Fact]
    public void APluginQuotingALogPrefixDoesNotBecomeAnError()
    {
        // Same protection, without a player involved: plugins echo log lines all the time.
        Assert.Equal(ConsoleLineKind.Info,
            Of("[12:34:56] [Server thread/INFO]: last error was [12:00:00 ERROR]: timeout"));
    }

    [Fact]
    public void AQuotedLevelIsIgnoredEvenWhenThePrefixHasNoneOfItsOwn()
    {
        // The boundary the prefix bound actually exists for, and the only input that shows it. When
        // the real prefix carries a level — which vanilla and Paper always do — it is the first
        // thing the search finds and the bound changes nothing. It is only when the prefix has no
        // level that an "[…/ERROR]" further along the line would be picked up instead, and an
        // ordinary message would be painted as a failure.
        Assert.Equal(ConsoleLineKind.Info,
            Of("[12:34:56] [Render thread]: replaying [main/ERROR] from the crash report"));
    }

    // --- Stack traces ---

    [Theory]
    [InlineData("\tat net.minecraft.server.MinecraftServer.run(MinecraftServer.java:100)")]
    [InlineData("    at java.base/java.lang.Thread.run(Thread.java:840)")]
    [InlineData("Caused by: java.lang.NullPointerException")]
    [InlineData("... 12 more")]
    public void AStackTraceIsPartOfTheError(string line)
    {
        // These carry no level of their own. Left as ordinary output, the one line that says what
        // broke ends up buried in forty that look routine.
        Assert.Equal(ConsoleLineKind.Error, Of(line));
    }

    // --- Chat, and the trap it sets ---

    [Fact]
    public void ChatIsRecognisedByTheNameTag()
    {
        Assert.Equal(ConsoleLineKind.Chat, Of("[12:34:56] [Server thread/INFO]: <Alice> hola a todos"));
    }

    [Fact]
    public void SomebodySayingSomebodyJoinedIsNotSomebodyJoining()
    {
        // The trap. Chat can contain a perfect copy of any other message, so a line that merely
        // quotes "joined the game" must stay chat — otherwise the player filter fills with events
        // that never happened, and so would the player list.
        Assert.Equal(ConsoleLineKind.Chat,
            Of("[12:34:56] [Server thread/INFO]: <Bob> Alice joined the game"));
    }

    [Fact]
    public void SomethingThatOnlyLooksLikeANameTagIsNotChat()
    {
        // "<" alone is not a chat tag: the name has to be a real Minecraft name.
        Assert.NotEqual(ConsoleLineKind.Chat,
            Of("[12:34:56] [Server thread/INFO]: <-- shutting down"));
    }

    // --- Player events ---

    [Fact]
    public void JoiningLeavingAndDyingAreAllTheSameKind()
    {
        Assert.Equal(ConsoleLineKind.Players,
            Of("[12:34:56] [Server thread/INFO]: Alice joined the game"));
        Assert.Equal(ConsoleLineKind.Players,
            Of("[12:34:56] [Server thread/INFO]: Alice left the game"));
        Assert.Equal(ConsoleLineKind.Players,
            Of("[12:34:56] [Server thread/INFO]: Alice was slain by Bob"));
    }

    [Fact]
    public void OrdinaryOutputIsJustInfo()
    {
        Assert.Equal(ConsoleLineKind.Info,
            Of("[12:34:56] [Server thread/INFO]: Done (17.000s)! For help, type \"help\""));
        Assert.Equal(ConsoleLineKind.Info,
            Of("[12:34:56] [Server thread/INFO]: Starting minecraft server version 1.21.1"));
    }

    [Fact]
    public void ALineWithNoPrefixAtAllIsNotAGuess()
    {
        // Plenty of output has no log prefix. It is ordinary until something says otherwise.
        Assert.Equal(ConsoleLineKind.Info, Of("Picked up JAVA_TOOL_OPTIONS: -Dfile.encoding=UTF-8"));
        Assert.Equal(ConsoleLineKind.Info, Of(""));
    }

    // --- The source outranks the text ---

    [Fact]
    public void AnythingOnStandardErrorIsAnError()
    {
        // The one severity signal that needs no parsing and that no plugin or locale can reword.
        Assert.Equal(ConsoleLineKind.Error,
            ConsoleLineClassifier.Classify("Exception in thread \"main\"", ConsoleSource.Stderr));

        // Even something that reads as perfectly ordinary.
        Assert.Equal(ConsoleLineKind.Error,
            ConsoleLineClassifier.Classify("[12:34:56 INFO]: hello", ConsoleSource.Stderr));
    }

    [Fact]
    public void TheAppsOwnMessagesAreNeverRead()
    {
        // Tagged at the source, never recognised by their text — the prefixes live inside the resx
        // values and are translated with them, so a classifier keyed on "[Launcher]" would work in
        // Spanish and quietly stop working in German.
        Assert.Equal(ConsoleLineKind.Launcher,
            ConsoleLineClassifier.Classify("[Launcher] Starting server…", ConsoleSource.Launcher));

        Assert.Equal(ConsoleLineKind.Launcher,
            ConsoleLineClassifier.Classify("[12:34:56] [Server thread/ERROR]: x", ConsoleSource.Launcher));
    }

    // --- NameBefore, moved here from the view model ---

    [Fact]
    public void TheNameIsTakenOnlyFromARealLogEntry()
    {
        Assert.Equal("Alice", ConsoleLineClassifier.NameBefore(
            "[12:34:56] [Server thread/INFO]: Alice joined the game", " joined the game"));

        // Quoted in chat: the text between the prefix and the marker is "<Bob> Alice", not a name.
        Assert.Null(ConsoleLineClassifier.NameBefore(
            "[12:34:56] [Server thread/INFO]: <Bob> Alice joined the game", " joined the game"));

        // Not a valid Minecraft name.
        Assert.Null(ConsoleLineClassifier.NameBefore(
            "[12:34:56] [Server thread/INFO]: some long sentence joined the game", " joined the game"));
    }
}
