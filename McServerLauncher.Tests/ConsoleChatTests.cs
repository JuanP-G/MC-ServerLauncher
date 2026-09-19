using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Every shape a message to the other players takes in the log, not only <c>&lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reported as <c>say</c> coming out white: the server's own <c>[Server] hola</c> was read as
/// ordinary output. The same gap hid <c>/me</c>, a player's <c>/say</c>, and — the one people were
/// already living with — all chat on a server without secure profiles, which the server logs as
/// <c>[Not Secure] &lt;Bob&gt; hola</c>.
/// </para>
/// <para>
/// The trap is <c>[Name] message</c>: on Paper every plugin logs as <c>[LuckPerms] Loading…</c>,
/// and a plugin name is a valid player name. So a bracketed name only counts when that player is
/// connected right now.
/// </para>
/// </remarks>
public class ConsoleChatTests
{
    private static readonly IReadOnlySet<string> Nobody = new HashSet<string>();

    private static IReadOnlySet<string> Online(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static ConsoleLineKind Of(string line, IReadOnlySet<string>? online = null) =>
        ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout, online: online ?? Nobody);

    [Theory]
    [InlineData("[21:10:03] [Server thread/INFO]: [Server] hola a todos")]   // say, from the app
    [InlineData("[21:10:03 INFO]: [Server] hola a todos")]                   // the same on Paper
    [InlineData("[21:10:03] [Server thread/INFO]: [Rcon] reinicio en 5 min")]
    [InlineData("[21:10:03] [Server thread/INFO]: [@] el bloque de comandos habla")]
    public void WhatTheServerSaysIsAMessage(string line)
    {
        Assert.Equal(ConsoleLineKind.Chat, Of(line));
    }

    [Fact]
    public void ChatWithoutASignatureIsStillChat()
    {
        Assert.Equal(ConsoleLineKind.Chat, Of("[21:10:03] [Server thread/INFO]: [Not Secure] <Bob> hola"));
    }

    [Fact]
    public void AConnectedPlayersSayAndMeAreMessages()
    {
        var online = Online("Alice");

        Assert.Equal(ConsoleLineKind.Chat, Of("[21:10:03] [Server thread/INFO]: [Alice] mirad esto", online));
        Assert.Equal(ConsoleLineKind.Chat, Of("[21:10:03] [Server thread/INFO]: * Alice saluda", online));
    }

    [Theory]
    [InlineData("[21:10:03 INFO]: [LuckPerms] Loading configuration...")]
    [InlineData("[21:10:03 INFO]: [Alice] mirad esto")]            // Alice is not on the server
    [InlineData("[21:10:03 INFO]: * Alice saluda")]
    public void ABracketedNameNobodyIsPlayingAsIsAPluginNotAPlayer(string line)
    {
        Assert.Equal(ConsoleLineKind.Info, Of(line));
    }

    [Fact]
    public void TheServerQuotingAJoinIsNotAJoin()
    {
        // `say Alice joined the game` must not put Alice on the player list.
        const string line = "[21:10:03] [Server thread/INFO]: [Server] Alice joined the game";

        Assert.Equal(ConsoleLineKind.Chat, Of(line));
        Assert.Null(ConsoleLineClassifier.NameBefore(line, " joined the game"));
    }

    [Fact]
    public void ChatOfSaysWhoAndWhat()
    {
        Assert.Equal(("Bob", "hola"),
            ConsoleLineClassifier.ChatOf("[21:10:03] [Server thread/INFO]: [Not Secure] <Bob> hola", Nobody));
        Assert.Equal(("Server", "hola a todos"),
            ConsoleLineClassifier.ChatOf("[21:10:03] [Server thread/INFO]: [Server] hola a todos", Nobody));
        Assert.Equal(("Alice", "saluda"),
            ConsoleLineClassifier.ChatOf("[21:10:03] [Server thread/INFO]: * Alice saluda", Online("Alice")));
        Assert.Null(ConsoleLineClassifier.ChatOf("[21:10:03] [Server thread/INFO]: Done (3.2s)!", Nobody));
    }

    [Fact]
    public void WithoutKnowingWhoIsOnlineOnlyTheCertainShapesCount()
    {
        // Callers that have no player list (tests, imports) get the safe answer.
        Assert.Equal(ConsoleLineKind.Chat,
            ConsoleLineClassifier.Classify("[21:10:03] [Server thread/INFO]: [Server] hola", ConsoleSource.Stdout));
        Assert.Equal(ConsoleLineKind.Info,
            ConsoleLineClassifier.Classify("[21:10:03] [Server thread/INFO]: [Alice] hola", ConsoleSource.Stdout));
    }
}
