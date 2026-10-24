using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Paper's coloured console lines, as the app received them: players joined and the app did not see them.
/// </summary>
public class AnsiEscapesTests
{
    private const string Esc = "\x1b";

    [Fact]
    public void Colour_and_reset_sequences_are_removed()
    {
        var line = "[16:41:52 INFO]: " + Esc + "[38;5;11mBot1 joined the game" + Esc + "[0m";

        Assert.Equal("[16:41:52 INFO]: Bot1 joined the game", AnsiEscapes.Strip(line));
    }

    [Fact]
    public void A_line_without_escapes_is_returned_as_it_is()
    {
        const string line = "[12:00:00] [Server thread/INFO]: <Bob> hello [1m]";

        Assert.Same(line, AnsiEscapes.Strip(line));
    }

    [Fact]
    public void Bold_and_several_codes_in_one_line_all_go()
    {
        var line = Esc + "[1m" + Esc + "[38;5;11m?" + Esc + "[0m text " + Esc + "[0;32mgreen" + Esc + "[m";

        Assert.Equal("? text green", AnsiEscapes.Strip(line));
    }

    [Fact]
    public void A_coloured_join_is_not_seen_as_a_join_until_it_is_stripped_and_is_after()
    {
        var raw = "[16:41:52 INFO]: " + Esc + "[38;5;11mBot1 joined the game" + Esc + "[0m";

        Assert.Null(ConsoleLineClassifier.NameBefore(raw, " joined the game"));
        Assert.Equal("Bot1", ConsoleLineClassifier.NameBefore(AnsiEscapes.Strip(raw), " joined the game"));
        Assert.Equal(ConsoleLineKind.Players,
            ConsoleLineClassifier.Classify(AnsiEscapes.Strip(raw), ConsoleSource.Stdout, null, null));
    }

    [Fact]
    public void A_coloured_leave_is_seen_too()
    {
        var raw = "[16:41:52 INFO]: " + Esc + "[38;5;11mBot1 left the game" + Esc + "[0m";

        Assert.Equal("Bot1", ConsoleLineClassifier.NameBefore(AnsiEscapes.Strip(raw), " left the game"));
    }

    [Fact]
    public void Stripping_does_not_let_chat_pass_for_a_join()
    {
        // The escape is gone, but what is left still starts with the chat's angle brackets.
        var raw = "[16:41:52 INFO]: <Bob> " + Esc + "[0mAlice joined the game";

        Assert.Null(ConsoleLineClassifier.NameBefore(AnsiEscapes.Strip(raw), " joined the game"));
    }
}
