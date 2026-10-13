using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A world saved by a newer Minecraft does not start on an older server.
/// </summary>
/// <remarks>
/// A world played on 26.3 was opened with 26.1 and then 26.2. Minecraft never downgrades a world:
/// every chunk it could not read loaded as air ("Recoverable errors when loading section … Not a
/// map"), the spawn came back nearly empty, and the app had started it without a word.
/// </remarks>
[Collection("avalonia")]
public class NewerWorldTests(AvaloniaFixture ui) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-newer-" + Guid.NewGuid().ToString("N"));

    // --- level.dat as Minecraft writes it, as far as the version goes ---

    private static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new[] { (byte)(b.Length >> 8), (byte)b.Length }.Concat(b).ToArray();
    }

    private static byte[] Named(byte type, string name, byte[] payload) => [type, .. Str(name), .. payload];

    private static byte[] Int(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        return b;
    }

    private static byte[] LevelDat(string versionName)
    {
        byte[] version = [.. Named(3, "Id", Int(4500)), .. Named(8, "Name", Str(versionName)), 0];
        byte[] data = [.. Named(3, "DataVersion", Int(4500)), .. Named(10, "Version", version), 0];
        byte[] root = Named(10, "", [.. Named(10, "Data", data), 0]);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(root);
        return ms.ToArray();
    }

    private ServerConfig Config(string serverVersion, string? worldVersion)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "world"));
        if (worldVersion is not null)
            File.WriteAllBytes(Path.Combine(_folder, "world", "level.dat"), LevelDat(worldVersion));
        return new ServerConfig { Name = "survival", FolderPath = _folder, GameVersion = serverVersion };
    }

    // --- The rule ---

    [Theory]
    [InlineData("26.3", "26.1", true)]
    [InlineData("1.21.4", "1.21.1", true)]
    [InlineData("1.21.1", "1.21.4", false)]   // an upgrade is how Minecraft is meant to be used
    [InlineData("26.2", "26.2", false)]
    [InlineData("1.21", "1.21.0", false)]     // the same release, written two ways
    [InlineData("25w14a", "1.21.4", false)]   // a snapshot: can't tell, so it never blocks
    [InlineData("1.21.5-pre1", "1.21.4", false)]
    [InlineData(null, "1.21.4", false)]
    [InlineData("26.3", "", false)]
    public void OnlyAPlainNewerReleaseCounts(string? world, string server, bool newer) =>
        Assert.Equal(newer, WorldVersionCheck.IsNewer(world, server));

    [Fact]
    public void TheVersionIsReadFromLevelDat()
    {
        Config("26.1", "26.3");

        Assert.Equal("26.3", WorldVersionCheck.SavedBy(Path.Combine(_folder, "world")));
    }

    [Fact]
    public void ANewerWorldIsReportedWithItsVersion() =>
        Assert.Equal("26.3", WorldVersionCheck.NewerThanServer(Config("26.1", "26.3")));

    [Fact]
    public void NoWorldYetIsNothingToProtect() =>
        Assert.Null(WorldVersionCheck.NewerThanServer(Config("26.1", worldVersion: null)));

    // --- The server ---

    private ServerViewModel Server(ServerConfig config)
    {
        ServerViewModel? server = null;
        ui.Run(() => server = new ServerViewModel(config));
        ui.Run(server!.RefreshFromDisk);
        return server!;
    }

    [Fact]
    public async Task ItDoesNotStartAndSaysWhy()
    {
        var server = Server(Config("26.1", "26.3"));

        Assert.True(server.HasNewerWorld);
        Assert.False(server.CanStart);
        Assert.Contains("26.3", server.NewerWorldText);

        // Restart and a wake go straight here, past the button.
        ui.Run(() => server.StartInternalAsync(isAutoRestart: true).GetAwaiter().GetResult());
        await Task.Delay(100);

        var expected = string.Format(Localizer.Get("Msg_WorldNewerFmt"), "26.3", "26.1");
        Assert.Contains(server.ConsoleLines, l => l.Text == expected);
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void GoingBackToTheWorldsVersionClearsIt()
    {
        var config = Config("26.1", "26.3");
        var server = Server(config);

        ui.Run(() => config.GameVersion = "26.3");

        Assert.False(server.HasNewerWorld);
        Assert.True(server.CanStart);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
