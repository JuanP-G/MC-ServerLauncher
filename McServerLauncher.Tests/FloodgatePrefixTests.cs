using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Floodgate's <c>username-prefix</c> is a dot unless the owner changed it, and the app accepted the
/// dot alone. On a server set to <c>*</c> a Bedrock player is <c>*Steve</c> in the log, and was
/// never on the player list or in the history. The prefix is now the server's own, read from
/// Floodgate's config.
/// </summary>
public class FloodgatePrefixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-prefix-" + Path.GetRandomFileName());

    public FloodgatePrefixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    private const string Log = "[21:10:03 INFO]: ";
    private static readonly IReadOnlySet<string> Nobody = new HashSet<string>();

    // --- which prefixes are accepted ---

    [Theory]
    [InlineData(".")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("~")]
    [InlineData("!")]
    [InlineData("#")]
    [InlineData("$")]
    [InlineData("^")]
    [InlineData("=")]
    [InlineData("**")]
    [InlineData("-*-")]
    public void APrefixOfSafeCharactersIsUsable(string prefix) => Assert.True(PlayerNames.IsUsablePrefix(prefix));

    /// <summary>
    /// Each of these would do something in a command, a path or the chat detector: a second
    /// argument, a selector, a separator, the brackets chat is recognised by.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(". ")]
    [InlineData("\n")]
    [InlineData("@")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData(":")]
    [InlineData("<")]
    [InlineData("[")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("%")]
    [InlineData(";")]
    [InlineData("|")]
    [InlineData("&")]
    [InlineData("....")]
    [InlineData("BE_")]
    [InlineData("§")]
    public void AnythingElseIsNotAPrefix(string? prefix) => Assert.False(PlayerNames.IsUsablePrefix(prefix));

    // --- the name rule with a server's prefix ---

    [Theory]
    [InlineData("*Steve", "*")]
    [InlineData("-Steve", "-")]
    [InlineData("**Steve_2", "**")]
    [InlineData(".Steve", ".")]
    [InlineData("Alice", "*")]    // a Java player on the same server
    [InlineData("Alice", "")]
    [InlineData("BE_Steve", "")]  // a prefix of letters makes an ordinary name
    public void ANameWithTheServersPrefixIsOnTheServer(string name, string prefix) =>
        Assert.True(PlayerNames.IsOnServer(name, prefix));

    [Theory]
    [InlineData(".Steve", "*")]     // another server's prefix
    [InlineData("*Steve", ".")]
    [InlineData("*Steve", "")]
    [InlineData("**Steve", "*")]    // the prefix once, not as often as it fits
    [InlineData("*", "*")]          // a prefix and no name
    [InlineData("*Ste ve", "*")]
    [InlineData("*Steve\n", "*")]
    [InlineData("*../x", "*")]
    [InlineData("@a", "@")]         // not a usable prefix, so not a name however it is handed in
    [InlineData("/Steve", "/")]
    [InlineData("<Steve", "<")]
    public void AnythingElseIsNot(string name, string prefix) =>
        Assert.False(PlayerNames.IsOnServer(name, prefix));

    [Fact]
    public void WithoutAPrefixHandedInTheDotStillWorks()
    {
        Assert.True(PlayerNames.IsOnServer(".Steve"));
        Assert.False(PlayerNames.IsOnServer("*Steve"));
        Assert.False(PlayerNames.IsOnServer("..Steve"));
        Assert.False(PlayerNames.IsValid("*Steve"));   // the strict rule is untouched
    }

    // --- every detector, on a server whose prefix is * ---

    [Fact]
    public void JoinsLeavesChatDeathsAndAdvancementsAreReadWithTheServersPrefix()
    {
        Assert.Equal("*Steve", ConsoleLineClassifier.NameBefore(Log + "System chat: *Steve joined the game",
            " joined the game", "*"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Join, "*Steve", null),
            PlayerEventParser.Parse(Log + "*Steve joined the game", Nobody, "*"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Leave, "*Steve", null),
            PlayerEventParser.Parse(Log + "*Steve left the game", Nobody, "*"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Chat, "*Steve", "hola"),
            PlayerEventParser.Parse(Log + "<*Steve> hola", Nobody, "*"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Death, "*Steve", "*Steve was slain by Zombie"),
            PlayerEventParser.Parse(Log + "*Steve was slain by Zombie", Nobody, "*"));
        Assert.Equal(new PlayerEvent(PlayerEventKind.Advancement, "*Steve", "Stone Age"),
            PlayerEventParser.Parse(Log + "*Steve has made the advancement [Stone Age]", Nobody, "*"));
        Assert.Equal(("*Steve", "00000000-0000-0000-0009-01f64f65c7c3"), PlayerEventParser.UuidOf(
            Log + "UUID of player *Steve is 00000000-0000-0000-0009-01F64F65C7C3", "*"));
        Assert.Equal(ConsoleLineKind.Players,
            ConsoleLineClassifier.Classify(Log + "*Steve joined the game", ConsoleSource.Stdout, floodgatePrefix: "*"));
    }

    [Fact]
    public void WithTheDefaultDotThoseSameLinesAreNotEvents()
    {
        Assert.Null(ConsoleLineClassifier.NameBefore(Log + "*Steve joined the game", " joined the game"));
        Assert.Null(PlayerEventParser.Parse(Log + "*Steve has made the advancement [Stone Age]", Nobody));
        Assert.Null(PlayerEventParser.UuidOf(Log + "UUID of player *Steve is 00000000-0000-0000-0009-01F64F65C7C3"));
    }

    /// <summary>
    /// The star is also how <c>/me</c> is written (<c>* Bob waves</c>), and chat can say anything.
    /// Neither becomes an event on a server whose prefix is a star.
    /// </summary>
    [Theory]
    [InlineData("<Bob> *Steve left the game")]
    [InlineData("* Bob left the game")]
    [InlineData("* *Steve left the game")]
    [InlineData("[Bob] *Steve left the game")]
    [InlineData("**Steve left the game")]
    public void TypingAPrefixedNameInChatIsNotAnEvent(string message) =>
        Assert.Null(ConsoleLineClassifier.NameBefore(Log + message, " left the game", "*"));

    [Fact]
    public void AMeFromAPrefixedPlayerIsChatWhenTheyAreOnline()
    {
        var online = new HashSet<string> { "*Steve" };

        Assert.Equal(("*Steve", "saluda"), ConsoleLineClassifier.ChatOf(Log + "* *Steve saluda", online, "*"));
        Assert.Null(ConsoleLineClassifier.ChatOf(Log + "* *Steve saluda", Nobody, "*"));
    }

    // --- reading Floodgate's config ---

    [Theory]
    [InlineData("username-prefix: \".\"", ".")]
    [InlineData("username-prefix: \"*\"", "*")]
    [InlineData("username-prefix: '*'", "*")]
    [InlineData("username-prefix: *", "*")]
    [InlineData("username-prefix: \"-\"   # for my server", "-")]
    [InlineData("  username-prefix:\"+\"", "+")]
    [InlineData("key-file-name: key.pem\r\nusername-prefix: \"~\"\r\nreplace-spaces: true", "~")]
    [InlineData("username-prefix: \"\"", "")]       // none: Bedrock names are ordinary ones
    [InlineData("username-prefix: \"BE_\"", "")]    // letters: the result is an ordinary name
    [InlineData("username-prefix: \"@\"", "")]      // not accepted: those names stay unrecognised
    [InlineData("username-prefix: \"a b\"", "")]
    [InlineData("username-prefix: \".....\"", "")]
    public void ThePrefixIsReadFromTheConfig(string yaml, string expected) =>
        Assert.Equal(expected, FloodgateConfig.PrefixIn(yaml));

    [Theory]
    [InlineData("")]
    [InlineData("replace-spaces: true")]
    [InlineData("# username-prefix: \"*\"")]
    [InlineData("my-username-prefix: \"*\"")]
    public void AConfigWithoutTheKeySaysNothing(string yaml) => Assert.Null(FloodgateConfig.PrefixIn(yaml));

    [Theory]
    [InlineData("plugins")]   // Paper and Purpur
    [InlineData("config")]    // Fabric and NeoForge
    public void ItIsReadFromWhereEachKindOfFloodgateKeepsIt(string root)
    {
        Directory.CreateDirectory(Path.Combine(_dir, root, "floodgate"));
        File.WriteAllText(Path.Combine(_dir, root, "floodgate", "config.yml"), "username-prefix: \"*\"\n");

        Assert.Equal("*", FloodgateConfig.ReadUsernamePrefix(new ServerConfig { FolderPath = _dir }));
    }

    [Fact]
    public void WithoutFloodgateTheDefaultIsTheDot() =>
        Assert.Equal(".", FloodgateConfig.ReadUsernamePrefix(new ServerConfig { FolderPath = _dir }));

    // --- the history, where a name becomes a file ---

    [Theory]
    [InlineData("*Steve")]
    [InlineData("-Steve")]
    [InlineData(".Steve")]
    [InlineData("Alice")]
    public void AStorableNameIsKept(string name) => Assert.True(PlayerNames.IsStorable(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("*/x")]
    [InlineData("****Steve")]
    [InlineData("C:x")]
    [InlineData("@a")]
    [InlineData("*Ste ve")]
    public void WhatCouldBecomeAPathIsNot(string? name) => Assert.False(PlayerNames.IsStorable(name));

    /// <summary>A star cannot be in a file name on Windows; the history writes it out.</summary>
    [Fact]
    public void TheHistoryKeepsAStarPrefixedPlayerInAFileOfItsOwn()
    {
        var root = Path.Combine(_dir, "history");
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        using var store = new PlayerHistoryStore(root, () => new PlayerHistorySettings().Clamped(), () => now);

        store.Record(new PlayerEvent(PlayerEventKind.Join, "*Steve", null), now);
        store.Record(new PlayerEvent(PlayerEventKind.Join, "Steve", null), now);

        Assert.Equal(1, store.Get("*Steve")?.Sessions);
        Assert.Equal(1, store.Get("Steve")?.Sessions);
        Assert.Single(store.Events("*Steve"));

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        Assert.All(files, f => Assert.StartsWith(root, Path.GetFullPath(f)));
        Assert.Contains(files, f => Path.GetFileName(f) == "%2asteve.jsonl");
        Assert.DoesNotContain(files, f => Path.GetFileName(f).Contains('*'));
    }

    [Fact]
    public void OldLogsAreImportedWithTheServersPrefix()
    {
        var root = Path.Combine(_dir, "history");
        using var store = new PlayerHistoryStore(root);

        var count = PlayerLogImporter.ImportLines(
        [
            "[10:05:00 INFO]: System chat: *Steve joined the game",
            "[11:05:00 INFO]: System chat: *Steve left the game"
        ], new DateTime(2026, 11, 1), store, "*");

        Assert.Equal(2, count);
        Assert.Equal(1, store.Get("*Steve")?.Sessions);
    }

    // --- what the owner types ---

    private sealed class FakeHost(string folder, string prefix) : IPlayerAdminHost
    {
        public ServerConfig Config { get; } = new() { Name = "T", FolderPath = folder };
        public bool IsProcessRunning { get; set; }
        public bool WhitelistEnabled { get; set; }
        public string FloodgatePrefix => prefix;
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
    public async Task KickAcceptsANameWithThisServersPrefixAndNoOther()
    {
        var host = new FakeHost(_dir, "*") { IsProcessRunning = true };

        await Admin(host).KickAsync("*Steve");
        await Admin(host).KickAsync(".Steve");
        await Admin(host).KickAsync("*Steve stop");
        await Admin(host).KickAsync("@a");

        Assert.Equal(new[] { "kick *Steve" }, host.Sent);
        Assert.Equal(3, host.Said.Count);
    }
}
