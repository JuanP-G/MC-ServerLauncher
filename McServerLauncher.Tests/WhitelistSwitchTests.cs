using System.Reflection;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A new server starts with the whitelist off, and the switch on the Players tab turns it on and off
/// without emptying it.
/// </summary>
/// <remarks>
/// <para>
/// Minecraft 26.3 changed its own default to <c>white-list=true</c>. The app only wrote the port and
/// the MOTD, so every server created for 26.3 locked everybody out, its owner included, behind an
/// empty list that nothing on the Players tab said was switched on.
/// </para>
/// <para>
/// And switching it off meant finding the setting in Configurar; emptying the list did not help,
/// and lost the names.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class WhitelistSwitchTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-whitelist-" + Guid.NewGuid()
        .ToString("N"));
    private string Properties => Path.Combine(_folder, "server.properties");

    private string? WhiteList() =>
        new ServerPropertiesService().Read(Properties).TryGetValue("white-list", out var v) ? v : null;

    [Fact]
    public void ANewServerIsOpen()
    {
        Directory.CreateDirectory(_folder);

        new ServerCreationService().WriteInitialProperties(_folder, 25565, "Survival");

        Assert.Equal("false", WhiteList());
    }

    [Fact]
    public void AFolderThatAlreadyHadAServerKeepsItsChoice()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Properties, "white-list=true\n");

        new ServerCreationService().WriteInitialProperties(_folder, 25565, "Survival");

        Assert.Equal("true", WhiteList());
    }

    private ServerViewModel Server(string properties)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Properties, properties);
        ServerViewModel? server = null;
        ui.Run(() => server = new ServerViewModel(new ServerConfig
        {
            Name = "survival",
            FolderPath = _folder
        }));
        return server!;
    }

    private static Task Run(ServerViewModel server, string method) =>
        (Task)typeof(ServerViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, [])!;

    [Fact]
    public async Task TheSwitchTurnsItOffAndKeepsTheNames()
    {
        var server = Server("white-list=true\nonline-mode=false\n");
        await new WhitelistService().AddAsync(_folder, "Steve", onlineMode: false);
        ui.Run(server.RefreshFromDisk);

        await Run(server, "ToggleWhitelist");

        Assert.Equal("false", WhiteList());
        Assert.False(server.WhitelistEnabled);
        Assert.Equal(new[] { "Steve" }, new WhitelistService().ReadNames(_folder));

        await Run(server, "ToggleWhitelist");

        Assert.Equal("true", WhiteList());
        Assert.True(server.WhitelistEnabled);
    }

    [Fact]
    public async Task AddingSomebodyTurnsItOn()
    {
        var server = Server("white-list=false\nonline-mode=false\n");
        server.NewWhitelistName = "Alex";

        await Run(server, "AddToWhitelist");

        Assert.Equal("true", WhiteList());
        Assert.True(server.WhitelistEnabled);
        Assert.Contains("Alex", server.WhitelistPlayers);
    }

    /// <summary>
    /// "online-mode=yes" is an offline server, as the server itself reads it (only "true" is true):
    /// the name goes in with the offline UUID, without asking Mojang. It used to count as online.
    /// </summary>
    [Fact]
    public async Task OnlineModeThatIsNotTrueMeansOffline()
    {
        var server = Server("white-list=false\nonline-mode=yes\n");
        server.NewWhitelistName = "Alex";

        await Run(server, "AddToWhitelist");

        Assert.Equal(new[] { "Alex" }, new WhitelistService().ReadNames(_folder));
        Assert.Contains(WhitelistService.OfflineUuid("Alex"),
            File.ReadAllText(Path.Combine(_folder, "whitelist.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
