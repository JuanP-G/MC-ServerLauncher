using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A Bedrock player joining through Floodgate shows up in the Java log with a dot in front of the
/// name (<c>.Steve</c>). Every detector used to refuse that name, so the player was never on the
/// list, the idle timer saw an empty server and stopped it with people playing.
/// </summary>
public class FloodgateNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-floodgate-" + Path.GetRandomFileName());

    public FloodgateNamesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    private const string Prefix = "[21:10:03] [Server thread/INFO]: ";
    private static readonly IReadOnlySet<string> Nobody = new HashSet<string>();

    [Theory]
    [InlineData(".Steve")]
    [InlineData(".a")]
    [InlineData(".Steve_2")]
    [InlineData(".1234567890123456")]
    public void AFloodgateNameIsAPlayerOnTheServer(string name) => Assert.True(PlayerNames.IsOnServer(name));

    [Theory]
    [InlineData("Alice")]   // a Java name is one too
    public void AJavaNameStaysAPlayerOnTheServer(string name) => Assert.True(PlayerNames.IsOnServer(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("..Steve")]
    [InlineData("../etc")]
    [InlineData(".a b")]
    [InlineData(".a-b")]
    [InlineData(".Steve\n")]
    [InlineData(".12345678901234567")]
    public void AnythingElseIsStillRefused(string? name) => Assert.False(PlayerNames.IsOnServer(name));

    /// <summary>The strict rule is untouched: it is what paths and commands for Java players rely on.</summary>
    [Fact]
    public void TheStrictRuleStillRefusesTheDot() => Assert.False(PlayerNames.IsValid(".Steve"));

    [Fact]
    public void AJoinAndALeaveNameTheBedrockPlayer()
    {
        Assert.Equal(".Steve", ConsoleLineClassifier.NameBefore(Prefix + ".Steve joined the game",
            " joined the game"));
        Assert.Equal(".Steve", ConsoleLineClassifier.NameBefore(Prefix + ".Steve left the game",
            " left the game"));
        Assert.True(ConsoleLineClassifier.IsPlayerEvent(Prefix + ".Steve joined the game"));
    }

    [Fact]
    public void QuotingABedrockJoinInChatIsStillNotJoining()
    {
        Assert.Null(ConsoleLineClassifier.NameBefore(Prefix + "<Bob> .Steve joined the game",
            " joined the game"));
    }

    [Fact]
    public void ChatAndDeathsOfABedrockPlayerAreRead()
    {
        Assert.Equal((".Steve", "hola"), ConsoleLineClassifier.ChatOf(Prefix + "<.Steve> hola", null));
        Assert.NotNull(DeathMessageDetector.Detect(Prefix + ".Steve was slain by Zombie"));
    }

    [Fact]
    public void TheHistoryParserReadsEachKindForABedrockPlayer()
    {
        Assert.Equal(new PlayerEvent(PlayerEventKind.Join, ".Steve", null),
            PlayerEventParser.Parse(Prefix + ".Steve joined the game", Nobody));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Leave, ".Steve", null),
            PlayerEventParser.Parse(Prefix + ".Steve left the game", Nobody));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Advancement, ".Steve", "Stone Age"),
            PlayerEventParser.Parse(Prefix + ".Steve has made the advancement [Stone Age]", Nobody));
        Assert.Equal((".Steve", "00000000-0000-0000-0009-01f64f65c7c3"), PlayerEventParser.UuidOf(
            "[21:10:02] [Geyser/INFO]: UUID of player .Steve is 00000000-0000-0000-0009-01F64F65C7C3"));
    }

    [Fact]
    public void TheHistoryKeepsABedrockPlayerInsideItsOwnFolder()
    {
        var root = Path.Combine(_dir, "history");
        var now = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        using var store = new PlayerHistoryStore(root, () => new PlayerHistorySettings().Clamped(), () => now);

        store.Record(new PlayerEvent(PlayerEventKind.Join, ".Steve", null), now);

        Assert.Equal(1, store.Get(".Steve")?.Sessions);
        Assert.Single(store.Events(".Steve"));
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        Assert.All(files, f => Assert.StartsWith(root, Path.GetFullPath(f)));
    }

    // --- What the owner types ---

    private sealed class FakeHost(string folder) : IPlayerAdminHost
    {
        public ServerConfig Config { get; } = new() { Name = "T", FolderPath = folder };
        public bool IsProcessRunning { get; set; }
        public bool WhitelistEnabled { get; set; }
        public readonly List<string> Sent = new();
        public readonly List<string> Said = new();

        public void SendToConsole(string command) => Sent.Add(command);
        public void Echo(string command) { }
        public void Say(string line) => Said.Add(line);
        public void RefreshWhitelist() { }
        public void RefreshPlayers() { }
        public void ShowWhitelistEnabled(bool on) => WhitelistEnabled = on;
        public void ResyncWhitelistSwitch() { }
    }

    private static PlayerAdmin Admin(FakeHost host) =>
        new(host, new ServerPropertiesService(), new WhitelistService(), new PlayersService());

    [Fact]
    public async Task KickBanAndOpAcceptABedrockNameOnARunningServer()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).KickAsync(".Steve");
        await Admin(host).BanAsync(".Steve");
        await Admin(host).OpAsync(".Steve");

        Assert.Equal(new[] { "kick .Steve", "ban .Steve", "op .Steve" }, host.Sent);
        Assert.Empty(host.Said);
    }

    /// <summary>
    /// The whitelist and the ban files stay strict: Floodgate keeps its own list, and a dotted name
    /// has no profile for the vanilla one to look up.
    /// </summary>
    [Fact]
    public async Task TheWhitelistStillTakesOnlyJavaNames()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).AddToWhitelistAsync(".Steve");

        Assert.Empty(host.Sent);
        Assert.Single(host.Said);
    }

    [Fact]
    public async Task ADotCannotSmuggleASecondArgumentIntoACommand()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).BanAsync(".Steve griefing");
        await Admin(host).BanAsync(".Steve\nstop");

        Assert.Empty(host.Sent);
        Assert.Equal(2, host.Said.Count);
    }
}
