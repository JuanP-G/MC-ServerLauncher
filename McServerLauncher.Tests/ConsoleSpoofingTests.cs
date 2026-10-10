using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What a player can make the app believe by typing in chat: nothing.
/// </summary>
/// <remarks>
/// <para>
/// Chat is the one part of the console somebody outside the machine writes, and it reaches the app
/// as an ordinary line with the server's own prefix in front. Every detector that reads a server
/// event used to look for its sentence <em>somewhere</em> in the line, so a sentence typed with a
/// prefix-shaped piece in front of it — <c>x: Bob left the game</c>, <c>x]: Saved the game</c> —
/// was taken for the real thing.
/// </para>
/// <para>
/// What that bought a player: taking themselves off the player list so the idle timer stopped a
/// server people were on, cutting a live backup short before the world was flushed, switching off
/// the auto-restart, and rewriting the remembered seed and a player's UUID. Each line below was
/// accepted before the fix.
/// </para>
/// </remarks>
public class ConsoleSpoofingTests
{
    private const string Vanilla = "[12:00:00] [Server thread/INFO]: ";
    private const string Paper = "[12:00:00 INFO]: ";
    private const string Forge = "[12:00:00] [Server thread/INFO] [minecraft/MinecraftServer]: ";

    private static readonly IReadOnlySet<string> BobOnline =
        new HashSet<string>(new[] { "Bob" }, StringComparer.OrdinalIgnoreCase);

    // --- Joins and leaves ---

    [Theory]
    [InlineData(Vanilla + "<Bob> x: Bob left the game")]
    [InlineData(Paper + "<Bob> x: Bob left the game")]
    [InlineData(Vanilla + "[Not Secure] <Bob> hi: Steve left the game")]
    [InlineData(Vanilla + "<Bob> a: b: Bob left the game")]
    public void ALeaveTypedInChatIsNotALeave(string line)
    {
        Assert.Null(ConsoleLineClassifier.NameBefore(line, " left the game"));
        Assert.Equal(ConsoleLineKind.Chat,
            ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout, online: BobOnline));
    }

    [Theory]
    [InlineData(Vanilla + "<Bob> x: Alice joined the game")]
    [InlineData(Vanilla + "* Bob says: Alice joined the game")]
    public void AJoinTypedInChatIsNotAJoin(string line)
    {
        Assert.Null(ConsoleLineClassifier.NameBefore(line, " joined the game"));
        Assert.NotEqual(PlayerEventKind.Join, PlayerEventParser.Parse(line, BobOnline)?.Kind);
    }

    [Theory]
    [InlineData(Vanilla + "Alice joined the game")]
    [InlineData(Paper + "Alice joined the game")]
    [InlineData(Forge + "Alice joined the game")]
    public void ARealJoinStillCountsInEveryLogShape(string line)
    {
        Assert.Equal("Alice", ConsoleLineClassifier.NameBefore(line, " joined the game"));
        Assert.Equal(ConsoleLineKind.Players,
            ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout, online: BobOnline));
    }

    // --- The save a live backup waits for ---

    [Theory]
    [InlineData(Vanilla + "<Bob> x]: Saved the game")]
    [InlineData(Paper + "<Bob> ]: Saved the world")]
    [InlineData(Vanilla + "[Not Secure] <Bob> x]: Saved the game")]
    public void ASaveTypedInChatDoesNotEndTheBackupsWait(string line) =>
        Assert.False(SaveConfirmation.IsSaveFinished(line));

    [Fact]
    public void TheRealSaveStillDoesInForgesShape() =>
        Assert.True(SaveConfirmation.IsSaveFinished(Forge + "Saved the game"));

    // --- The remembered seed and a player's UUID ---

    [Fact]
    public void ASeedTypedInChatIsNotRemembered() =>
        Assert.Null(WorldSeed.FromConsoleLine(Vanilla + "<Bob> x]: Seed: [123456]"));

    [Fact]
    public void AUuidTypedInChatIsNotTiedToAPlayer() =>
        Assert.Null(PlayerEventParser.UuidOf(
            Vanilla + "<Bob> x]: UUID of player Alice is 00000000-0000-0000-0000-000000000001"));

    [Fact]
    public void TheRealUuidLineStillIs() =>
        Assert.Equal(("Alice", "00000000-0000-0000-0000-000000000001"), PlayerEventParser.UuidOf(
            "[12:00:00] [User Authenticator #1/INFO]: UUID of player Alice is 00000000-0000-0000-0000-000000000001"));

    // --- The refusal that switches auto-restart off ---

    [Fact]
    public void ThePathRefusalTypedInChatIsChat()
    {
        // BukkitPathRule matches the sentence wherever it is, because the real refusal is printed
        // with no log prefix at all. What keeps chat out is the view model: a chat line never
        // reaches it, and a running server — the only kind a player can talk on — is not listened
        // to for it. This pins the half that lives here.
        const string line = Paper + "<Bob> cannot run server in a directory with ! or +";
        Assert.Equal(ConsoleLineKind.Chat,
            ConsoleLineClassifier.Classify(line, ConsoleSource.Stdout, online: BobOnline));
    }
}
