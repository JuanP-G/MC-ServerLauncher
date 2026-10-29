using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What taking over an existing folder writes, and what it leaves alone, now that it can be run
/// without the new-server panel.
/// </summary>
/// <remarks>
/// Only the adopt path is exercised: it is the one that needs no network (no version is picked, so no
/// Java is looked for). The create path downloads a server jar, which is what
/// <c>ServerJarInstaller</c>'s own tests cover.
/// </remarks>
public class ServerProvisioningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-adopt-" + Path.GetRandomFileName());

    public ServerProvisioningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    private ExistingServerForm Form() => new(
        Name: "Mine", Folder: _dir, Type: ServerType.Vanilla, GameVersion: "1.21.1", JarFile: "server.jar",
        MinRamGb: 2, MaxRamGb: 4, JavaPath: "ignored", Playit: false, Crossplay: false,
        MultiVersion: false, Hydraulic: false);

    [Fact]
    public async Task AdoptingWithoutAKnownVersionLooksForNoJavaAndKeepsTheSystemOne()
    {
        var log = new List<string>();

        var config = await new ServerProvisioningService().AdoptAsync(
            Form(), ServerDetection.Nothing with { Port = 25565 }, port: 25565, version: null, writeEula: false, log.Add);

        Assert.Equal("java", config.JavaPath);
        Assert.Equal(_dir, config.FolderPath);
        Assert.Empty(log);
    }

    [Fact]
    public async Task AChangedPortIsWrittenAndSaidSo()
    {
        File.WriteAllText(Path.Combine(_dir, "server.properties"), "server-port=25565\nmotd=hello\n");
        var log = new List<string>();

        await new ServerProvisioningService().AdoptAsync(
            Form(), ServerDetection.Nothing, port: 25570, version: null, writeEula: false, log.Add);

        var props = File.ReadAllText(Path.Combine(_dir, "server.properties"));
        Assert.Contains("server-port=25570", props);
        Assert.Contains("motd=hello", props);   // nothing else in the file is touched
        Assert.Single(log);
    }

    [Fact]
    public async Task TheEulaIsWrittenOnlyWhenTheBoxWasTicked()
    {
        var service = new ServerProvisioningService();

        await service.AdoptAsync(Form(), ServerDetection.Nothing, 25565, null, writeEula: false, _ => { });
        Assert.False(File.Exists(Path.Combine(_dir, "eula.txt")));

        await service.AdoptAsync(Form(), ServerDetection.Nothing, 25565, null, writeEula: true, _ => { });
        Assert.Contains("eula=true", File.ReadAllText(Path.Combine(_dir, "eula.txt")));
    }

    [Fact]
    public async Task AdoptingNeverWritesALaunchScriptOrAJar()
    {
        await new ServerProvisioningService().AdoptAsync(
            Form(), ServerDetection.Nothing, 25565, null, writeEula: true, _ => { });

        Assert.False(File.Exists(Path.Combine(_dir, "run.bat")));
        Assert.False(File.Exists(Path.Combine(_dir, "server.jar")));
    }
}
