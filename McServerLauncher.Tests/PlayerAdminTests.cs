using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// What an owner's actions on the players do to the server, with a fake in the place of the server:
/// the names that are refused before they reach a command, and the two paths — console while it
/// runs, files while it does not.
/// </summary>
public class PlayerAdminTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-admin-" + Path.GetRandomFileName());

    public PlayerAdminTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    private sealed class FakeHost(string folder) : IPlayerAdminHost
    {
        public ServerConfig Config { get; } = new() { Name = "T", FolderPath = folder };
        public bool IsProcessRunning { get; set; }
        public bool WhitelistEnabled { get; set; }
        public readonly List<string> Sent = new();
        public readonly List<string> Said = new();
        public int WhitelistRefreshes;
        public int PlayerRefreshes;

        public void SendToConsole(string command) => Sent.Add(command);
        public void Echo(string command) { }
        public void Say(string line) => Said.Add(line);
        public void RefreshWhitelist() => WhitelistRefreshes++;
        public void RefreshPlayers() => PlayerRefreshes++;
        public void ShowWhitelistEnabled(bool on) => WhitelistEnabled = on;
        public void ResyncWhitelistSwitch() { }
    }

    private static PlayerAdmin Admin(FakeHost host) =>
        new(host, new ServerPropertiesService(), new WhitelistService(), new PlayersService());

    [Theory]
    [InlineData("a b")]
    [InlineData("evil\nstop")]
    [InlineData("seventeen_chars_xx")]
    [InlineData("name;rm")]
    public async Task ANameThatIsNotAMinecraftNameNeverReachesTheConsole(string typed)
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).BanAsync(typed);
        await Admin(host).OpAsync(typed);
        await Admin(host).AddToWhitelistAsync(typed);

        Assert.Empty(host.Sent);
        Assert.Equal(3, host.Said.Count);
    }

    [Fact]
    public async Task ABlankNameIsIgnoredWithoutAMessage()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).KickAsync("   ");

        Assert.Empty(host.Sent);
        Assert.Empty(host.Said);
    }

    [Fact]
    public async Task OpNeedsTheServerRunningAndSaysSo()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = false };

        await Admin(host).OpAsync("Steve");

        Assert.Empty(host.Sent);
        Assert.Single(host.Said);
    }

    [Fact]
    public async Task OpOnARunningServerIsACommandAndRefreshesTheLists()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).OpAsync("Steve");

        Assert.Equal(["op Steve"], host.Sent);
        Assert.Equal(1, host.PlayerRefreshes);
    }

    [Fact]
    public async Task PardonOnAStoppedServerEditsTheBanFileInstead()
    {
        File.WriteAllText(Path.Combine(_dir, "banned-players.json"),
            """[{"uuid":"00000000-0000-0000-0000-000000000001","name":"Steve","created":"x","source":"s","expires":"forever","reason":"r"}]""");
        var host = new FakeHost(_dir) { IsProcessRunning = false };

        await Admin(host).PardonAsync("Steve");

        Assert.Empty(host.Sent);
        Assert.Empty(new PlayersService().ReadBanned(_dir));
        Assert.Equal(1, host.PlayerRefreshes);
    }

    [Fact]
    public async Task TheWhitelistSwitchOnAStoppedServerWritesTheFile()
    {
        File.WriteAllText(Path.Combine(_dir, "server.properties"), "white-list=false\n");
        var host = new FakeHost(_dir) { IsProcessRunning = false };

        await Admin(host).SetWhitelistAsync(true);

        Assert.Contains("white-list=true", File.ReadAllText(Path.Combine(_dir, "server.properties")));
        Assert.Empty(host.Sent);
    }

    [Fact]
    public async Task TheWhitelistSwitchOnARunningServerIsACommandAndTheSwitchFollowsIt()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).SetWhitelistAsync(true);

        Assert.Equal(["whitelist on"], host.Sent);
        Assert.True(host.WhitelistEnabled);
    }

    [Fact]
    public async Task SettingsSavedWhileRunningAreOnlyRememberedAndTheWhitelistGoesAsACommand()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = true };

        await Admin(host).FollowSavedPropertiesAsync(
            new Dictionary<string, string> { ["white-list"] = "true", ["motd"] = "hi" });

        Assert.Equal(["whitelist on"], host.Sent);
        Assert.False(File.Exists(Path.Combine(_dir, "server.properties")) &&
                     File.ReadAllText(Path.Combine(_dir, "server.properties")).Contains("white-list=true"));
    }

    [Fact]
    public async Task SettingsSavedWhileStoppedAreNotTheirConcern()
    {
        var host = new FakeHost(_dir) { IsProcessRunning = false };

        await Admin(host).FollowSavedPropertiesAsync(new Dictionary<string, string> { ["white-list"] = "true" });

        Assert.Empty(host.Sent);
    }
}
