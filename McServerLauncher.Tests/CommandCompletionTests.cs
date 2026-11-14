using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Completing console commands from what the server itself prints for <c>help</c>. The fixtures are
/// the real console output of a vanilla 26.2 server.
/// </summary>
public class CommandCompletionTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "console-help", name));

    private static CommandCatalog FromServer()
    {
        var catalog = new CommandCatalog();
        catalog.ReplaceWithServer(Fixture("vanilla-26.2-help.txt").Split('\n'));
        return catalog;
    }

    private static readonly CompletionContext Players =
        new(Online: ["Alex", "Steve"], Known: ["Alex", "Steve", "Notch"], Operators: ["Alex"]);

    private static List<string> Texts(Completion c) => c.Suggestions.Select(s => s.Text).ToList();

    [Fact]
    public void TheServersHelpIsReadIntoCommandsAndAliases()
    {
        var catalog = FromServer();
        Assert.True(catalog.FromServer);
        Assert.True(catalog.Knows("gamemode"));
        Assert.Equal("teleport", catalog.Resolve("tp"));
        Assert.Equal("experience", catalog.Resolve("xp"));
        Assert.Equal("gamemode <gamemode> [<target>]", catalog.UsagesOf("gamemode").Single());
        // the lines around the answer that are not help ("Saving chunks...") are not commands
        Assert.DoesNotContain(catalog.Names, n => n.StartsWith("Saving", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirstWordCompletesTheCommandWithTheCommonOnesFirst()
    {
        var c = CommandCompleter.Complete("ga", FromServer(), Players);
        Assert.Equal("gamemode", c.Suggestions[0].Text);
        Assert.Contains("gamerule", Texts(c));
        Assert.Equal(0, c.ReplaceFrom);
    }

    [Fact]
    public void ASlashInFrontWorksAsInGame()
    {
        var c = CommandCompleter.Complete("/gamem", FromServer(), Players);
        Assert.Equal("gamemode", c.Suggestions.Single().Text);
        Assert.Equal(1, c.ReplaceFrom);
    }

    [Fact]
    public void GamemodeOffersTheModesThenThePlayers()
    {
        var catalog = FromServer();
        var modes = CommandCompleter.Complete("gamemode cr", catalog, Players);
        Assert.Equal(["creative"], Texts(modes));
        Assert.Equal(9, modes.ReplaceFrom);
        Assert.Equal(0, modes.UsageWord);

        var who = CommandCompleter.Complete("gamemode creative ", catalog, Players);
        Assert.Equal(["Alex", "Steve", "Notch", "@a", "@p", "@r", "@s", "@e"], Texts(who));
        Assert.Equal(SuggestionKind.OnlineOperator, who.Suggestions[0].Kind);
        Assert.Equal(SuggestionKind.KnownPlayer, who.Suggestions[2].Kind);
        Assert.Equal(1, who.UsageWord);
    }

    [Fact]
    public void GameRulesComeWithThisVersionsNames()
    {
        var c = CommandCompleter.Complete("gamerule keep", FromServer(), Players);
        Assert.Contains("keep_inventory", Texts(c));
        Assert.DoesNotContain("keepInventory", Texts(c));
    }

    [Fact]
    public void AFoldedCommandAsksForItsBranchesAndThenUsesThem()
    {
        var catalog = FromServer();
        var first = CommandCompleter.Complete("time ", catalog, Players);
        Assert.Contains("set", Texts(first));
        Assert.Equal("time", first.DetailWanted);

        catalog.AddDetail("time", Fixture("vanilla-26.2-help-time.txt").Split('\n'));
        var deeper = CommandCompleter.Complete("time query ", catalog, Players);
        Assert.Equal(["time", "gametime"], Texts(deeper));
        Assert.Null(deeper.DetailWanted);
    }

    [Fact]
    public void ARestOfLineValueEndsTheSuggestions()
    {
        var c = CommandCompleter.Complete("say hola a to", FromServer(), Players);
        Assert.Empty(c.Suggestions);
        Assert.Equal("say <message>", c.Usage);
    }

    [Fact]
    public void AnAliasCompletesLikeItsCommand()
    {
        var c = CommandCompleter.Complete("tp St", FromServer(), Players);
        Assert.Contains("Steve", Texts(c));
    }

    [Fact]
    public void AWordTypedInFullNeedsNoSuggestion()
    {
        Assert.Empty(CommandCompleter.Complete("list", FromServer(), Players).Suggestions);
    }

    [Fact]
    public void AnUnknownCommandSuggestsNothing()
    {
        var c = CommandCompleter.Complete("nosuchcommand x", FromServer(), Players);
        Assert.Empty(c.Suggestions);
        Assert.Null(c.Usage);
    }

    [Fact]
    public void TheBuiltInListWorksBeforeTheServerIsAsked()
    {
        var builtIn = CommandCatalog.BuiltIn();
        Assert.False(builtIn.FromServer);
        Assert.Contains("whitelist", Texts(CommandCompleter.Complete("whi", builtIn, Players)));
    }

    [Theory]
    [InlineData("/ban: Adds player to banlist", "ban", null, "Adds player to banlist")]
    [InlineData("[13:00:01 INFO]: /essentials:home: Teleport to your home.", "essentials:home", null, "Teleport to your home.")]
    [InlineData("[13:26:30] [Server thread/INFO]: /tp -> teleport", "tp", "teleport", null)]
    public void BukkitAndAliasLinesAreRead(string line, string name, string? aliasOf, string? description)
    {
        var help = CommandCatalog.ParseHelpLine(line);
        Assert.NotNull(help);
        Assert.Equal(name, help.Value.Name);
        Assert.Equal(aliasOf, help.Value.AliasOf);
        Assert.Equal(description, help.Value.Description);
    }

    [Theory]
    [InlineData("[18:44:31] [Server thread/INFO]: Steve joined the game")]
    [InlineData("[18:45:10] [Server thread/INFO]: <Alex> /gamemode creative pls")]
    [InlineData("[18:45:10] [Server thread/INFO]: Saving chunks for level 'ServerLevel[world]'")]
    public void OtherLinesAreNotHelp(string line) => Assert.Null(CommandCatalog.ParseHelpLine(line));

    [Fact]
    public void UsageNotationIsRead()
    {
        var tokens = CommandUsage.Parse("effect give <targets> <effect> [<seconds>|infinite]");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(["give"], tokens[0].Literals);
        Assert.Equal(["targets"], tokens[1].Args);
        Assert.True(tokens[3].Optional);
        Assert.Equal(["infinite"], tokens[3].Literals);
        Assert.Equal(["seconds"], tokens[3].Args);
    }
}

/// <summary>Asking the server for its commands without the answer filling the console.</summary>
public class CommandHelpProbeTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheAnswerIsKeptBackAndTheFenceClosesIt()
    {
        var probe = new CommandHelpProbe();
        var lines = probe.Ask(null, T0)!;
        Assert.Equal("help", lines[0]);
        var fence = lines[1];

        var console = new[]
        {
            "[13:26:30] [Server thread/INFO]: Saving chunks for level 'ServerLevel[world]'",
            "[13:26:30] [Server thread/INFO]: /advancement (grant|revoke)",
            "[13:26:30] [Server thread/INFO]: Steve joined the game",
            "[13:26:30] [Server thread/INFO]: /gamemode <gamemode> [<target>]",
            "[13:26:31] [Server thread/INFO]: Unknown or incomplete command. See below for error",
            $"[13:26:31] [Server thread/INFO]: {fence}<--[HERE]",
        };
        var shown = new List<string>();
        CommandHelpProbe.Answer? answer = null;
        foreach (var line in console)
        {
            if (!probe.TryConsume(line, T0.AddSeconds(1), out var a)) shown.Add(line);
            answer ??= a;
        }

        Assert.Equal([console[0], console[2]], shown);   // only what was not the answer
        Assert.NotNull(answer);
        Assert.Null(answer.Value.Topic);
        Assert.Equal(2, answer.Value.Lines.Count);
        Assert.False(probe.IsAsking);
    }

    [Fact]
    public void NothingIsKeptBackWhenNothingWasAsked()
    {
        var probe = new CommandHelpProbe();
        Assert.False(probe.TryConsume("[13:00:00] [Server thread/INFO]: /gamemode <gamemode>", T0, out _));
    }

    [Fact]
    public void AQuestionThatGetsNoAnswerExpires()
    {
        var probe = new CommandHelpProbe();
        probe.Ask("time", T0);
        Assert.Null(probe.Ask("time", T0.AddSeconds(1)));   // one at a time
        var answer = probe.Expire(T0.AddSeconds(10));
        Assert.Equal("time", answer!.Value.Topic);
        Assert.False(probe.IsAsking);
    }
}
