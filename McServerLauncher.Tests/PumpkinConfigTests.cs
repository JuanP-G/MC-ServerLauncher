using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// <c>pumpkin.toml</c> standing in for the <c>server.properties</c> a Pumpkin server does not have.
/// </summary>
/// <remarks>
/// The file below is cut from the one Pumpkin 0.2.2 wrote on its first start: same tables, same key
/// names, same order. Only tables the app has no use for were left out.
/// </remarks>
public sealed class PumpkinConfigTests : IDisposable
{
    private const string WrittenByPumpkin = """
        seed = "1792838949275584738"
        default_difficulty = "Normal"
        default_level_name = "mundo"
        allow_chat_reports = false
        white_list = true
        enforce_whitelist = false

        [logging]
        enabled = true
        level = "info"
        timestamp_format = "[hour]:[minute]:[second]"

        [networking.query]
        enabled = false
        address = "0.0.0.0:25565"

        [networking.management]
        enabled = false
        address = "127.0.0.1:25585"

        [networking.java]
        enabled = true
        address = "0.0.0.0:25599"
        online_mode = false
        max_players = 12
        motd = "Un \"servidor\" de prueba"

        [networking.bedrock]
        enabled = true
        max_players = 1000
        motd = "A blazingly fast Pumpkin server!"

        [networking.bedrock.nethernet]
        enabled = true
        address = "0.0.0.0:19132"

        [telemetry]
        enabled = true
        """;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "mcsl-pumpkin-" + Guid.NewGuid().ToString("N"));

    private string Properties => Path.Combine(_folder, "server.properties");

    public PumpkinConfigTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private void WriteToml(string text = WrittenByPumpkin) =>
        File.WriteAllText(PumpkinConfig.PathIn(_folder), text);

    // --- reading ---

    [Fact]
    public void ThePortIsTheJavaAddressAndNoOtherTables()
    {
        // Three tables carry an "address". The query one comes first in the file and says 25565;
        // a reader that took the first "address" it met would send the wake listener, the tunnel
        // and the firewall rule to a port the server is not on.
        WriteToml();

        Assert.Equal(25599, new ServerPropertiesService().GetServerPort(Properties));
    }

    [Fact]
    public void TheRestComesUnderTheNamesServerPropertiesUses()
    {
        WriteToml();

        var props = new ServerPropertiesService().Read(Properties);

        Assert.Equal("Un \"servidor\" de prueba", props["motd"]);
        Assert.Equal("12", props["max-players"]);
        Assert.True(ServerPropertiesService.GetBool(props, "white-list", fallback: false));
        Assert.False(ServerPropertiesService.GetBool(props, "online-mode", fallback: true));
        Assert.Equal("mundo", ServerPropertiesService.GetLevelName(props));
    }

    [Fact]
    public void WhatTheFileLeavesOutIsPumpkinsDefaultNotMinecrafts()
    {
        // The file the app writes for a new server has four lines; Pumpkin fills in the rest only
        // once it has started. Until then the answers are still Pumpkin's: 1000 players, not 20.
        WriteToml("[networking.java]\naddress = \"0.0.0.0:25570\"\n");

        var props = new ServerPropertiesService().Read(Properties);

        Assert.Equal("25570", props["server-port"]);
        Assert.Equal("1000", props["max-players"]);
        Assert.Equal(PumpkinConfig.DefaultMotd, props["motd"]);
        Assert.Equal("world", ServerPropertiesService.GetLevelName(props));
        Assert.False(ServerPropertiesService.GetBool(props, "white-list", fallback: true));
    }

    [Theory]
    [InlineData("0.0.0.0:25565", 25565)]
    [InlineData("127.0.0.1:25599", 25599)]
    [InlineData("[::]:25570", 25570)]
    public void AnAddressGivesItsPort(string address, int port)
    {
        Assert.Equal(port, PumpkinConfig.PortOf(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("0.0.0.0:abc")]
    [InlineData("0.0.0.0:70000")]
    public void AnAddressWithNoUsablePortGivesNone(string? address)
    {
        Assert.Null(PumpkinConfig.PortOf(address));
    }

    [Fact]
    public void AnUnreadableFileIsTheDefaultsNotAnError()
    {
        WriteToml("this is = = not [toml\n\"");

        Assert.Equal(PumpkinConfig.DefaultPort, new ServerPropertiesService().GetServerPort(Properties));
    }

    // --- which folders it applies to ---

    [Fact]
    public void AJavaServerIsNeverAnsweredFromAStrayPumpkinToml()
    {
        // A pumpkin.toml dropped into a Paper server's folder changes nothing about that server.
        WriteToml();
        File.WriteAllText(Properties, "server-port=25577\nmotd=Paper\n");

        Assert.False(PumpkinConfig.IsServerFolder(_folder));
        Assert.Equal(25577, new ServerPropertiesService().GetServerPort(Properties));
    }

    [Fact]
    public void AFolderWithNeitherFileStillHasNoPort()
    {
        // What the rest of the app relies on for a server that has never been set up: null, which
        // means "do not check, do not listen", and not a port somebody else may be using.
        Assert.False(PumpkinConfig.IsServerFolder(_folder));
        Assert.Null(new ServerPropertiesService().GetServerPort(Properties));
        Assert.Empty(new ServerPropertiesService().Read(Properties));
    }

    [Fact]
    public void NoServerPropertiesIsEverWrittenIntoAPumpkinFolder()
    {
        // The whitelist switch and the settings pages write through this method. Creating the file
        // would be worse than refusing: Pumpkin never reads it, and from then on every read here
        // would be answered from it instead of from the real configuration.
        WriteToml();

        Assert.Throws<UserFacingException>(() => new ServerPropertiesService().Update(Properties,
            new Dictionary<string, string> { ["white-list"] = "true" }));

        Assert.False(File.Exists(Properties));
        Assert.Equal(25599, new ServerPropertiesService().GetServerPort(Properties));
    }

    // --- the player lists ---

    [Fact]
    public void ThePlayerListsOfAPumpkinServerAreInItsDataFolder()
    {
        WriteToml();
        Directory.CreateDirectory(Path.Combine(_folder, "data"));
        // The shape Pumpkin's Op and WhitelistEntry serialise to (pumpkin-config/src/op.rs).
        File.WriteAllText(Path.Combine(_folder, "data", "ops.json"),
            """[{"uuid":"db74c940-d447-e877-d119-df613edd2700","name":"Alex","level":4,"bypassesPlayerLimit":false}]""");
        File.WriteAllText(Path.Combine(_folder, "data", "whitelist.json"),
            """[{"uuid":"db74c940-d447-e877-d119-df613edd2700","name":"Alex"}]""");
        // A leftover in the root must not be what is read.
        File.WriteAllText(Path.Combine(_folder, "ops.json"), """[{"uuid":"x","name":"Nobody"}]""");

        Assert.Equal(["Alex"], new PlayersService().ReadOps(_folder));
        Assert.Equal(["Alex"], new WhitelistService().ReadNames(_folder));
    }

    [Fact]
    public async Task AddingToTheWhitelistWritesWhereThePumpkinServerReads()
    {
        WriteToml();
        Directory.CreateDirectory(Path.Combine(_folder, "data"));

        await new WhitelistService().AddAsync(_folder, "Alex", onlineMode: false);

        Assert.True(File.Exists(Path.Combine(_folder, "data", "whitelist.json")));
        Assert.False(File.Exists(Path.Combine(_folder, "whitelist.json")));
        Assert.Equal(["Alex"], new WhitelistService().ReadNames(_folder));
    }

    [Fact]
    public void AJavaServersListsStayInItsRoot()
    {
        File.WriteAllText(Properties, "server-port=25565\n");
        File.WriteAllText(Path.Combine(_folder, "ops.json"), """[{"uuid":"x","name":"Alex","level":4}]""");

        Assert.Equal(_folder, PlayerFiles.FolderOf(_folder));
        Assert.Equal(["Alex"], new PlayersService().ReadOps(_folder));
    }

    // --- the first file of a new server ---

    [Fact]
    public void ANewServerGetsItsPortItsNameAndItsSeed()
    {
        Assert.True(PumpkinConfig.WriteInitial(_folder, 25570, "Mi servidor - MC Server Launcher", "  abc123 "));

        var toml = PumpkinConfig.Read(PumpkinConfig.PathIn(_folder));
        Assert.Equal("abc123", toml["seed"]);
        Assert.Equal("0.0.0.0:25570", toml["networking.java.address"]);
        Assert.Equal("Mi servidor - MC Server Launcher", toml["networking.java.motd"]);
        Assert.Equal("Mi servidor - MC Server Launcher", toml["networking.bedrock.motd"]);

        Assert.Equal(25570, new ServerPropertiesService().GetServerPort(Properties));
    }

    [Fact]
    public void ANewServerSendsNoTelemetryUntilItsOwnerSaysSo()
    {
        // Pumpkin's default is on. The app sends nothing anywhere unasked, and neither does a
        // server it sets up.
        PumpkinConfig.WriteInitial(_folder, 25565, "x", seed: null);

        var toml = PumpkinConfig.Read(PumpkinConfig.PathIn(_folder));
        Assert.Equal("false", toml["telemetry.enabled"]);
        Assert.False(toml.ContainsKey("seed"));   // no seed asked for: Pumpkin picks a random one
    }

    [Fact]
    public void AnExistingFileIsNeverOverwritten()
    {
        WriteToml();

        Assert.False(PumpkinConfig.WriteInitial(_folder, 25570, "otro", "otra"));
        Assert.Equal(25599, new ServerPropertiesService().GetServerPort(Properties));
    }

    [Theory]
    [InlineData("a\"\n[telemetry]\nenabled = true\nx = \"")]
    [InlineData("a\\")]
    [InlineData("tab\there")]
    public void ANameCannotWriteItsOwnKeysIntoTheFile(string name)
    {
        // The server's name becomes its MOTD, and the name is typed by a person. A quote and a line
        // break would otherwise close the string and start a table of the typist's choosing.
        PumpkinConfig.WriteInitial(_folder, 25565, name, seed: name);

        var toml = PumpkinConfig.Read(PumpkinConfig.PathIn(_folder));
        Assert.Equal("false", toml["telemetry.enabled"]);
        Assert.Equal("0.0.0.0:25565", toml["networking.java.address"]);
        Assert.Equal(4, File.ReadAllLines(PumpkinConfig.PathIn(_folder)).Count(l => l.StartsWith('[') || l.StartsWith("seed")));
    }
}
