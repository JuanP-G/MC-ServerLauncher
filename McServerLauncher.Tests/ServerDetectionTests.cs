using System.IO.Compression;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Telling what a server folder is, from the files in it.
/// </summary>
/// <remarks>
/// <para>
/// This is what the create dialog's "use a folder that already exists" stands on: pick a folder,
/// and the type, the version, what to launch, the port and the memory come back filled in. Until
/// now only Forge and the "already known" case were tested; every other type was taken on trust —
/// which is how Purpur, a type this app creates itself, went undetected altogether.
/// </para>
/// <para>
/// Each case builds only the files that matter: jars made with <see cref="ZipArchive"/> carrying the
/// one entry the detector reads, or the <c>libraries/</c> directory with an args file in it.
/// </para>
/// </remarks>
public class ServerDetectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-detect-" + Guid.NewGuid().ToString("N"));

    public ServerDetectionTests() => Directory.CreateDirectory(_folder);

    private ServerDetection Detect(string? preferredJar = null) =>
        new ServerDetectionService().Detect(_folder, preferredJar);

    /// <summary>A jar whose version.json says which Minecraft it is, as vanilla, Paper and Purpur's do.</summary>
    private void VersionedJar(string name, string version) =>
        Jar(name, "version.json", $$"""{"id":"{{version}}","name":"{{version}}"}""");

    private void Jar(string name, string entry, string content)
    {
        using var zip = ZipFile.Open(Path.Combine(_folder, name), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
        writer.Write(content);
    }

    private void File(string relative, string content)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    // --- What it is ---

    [Fact]
    public void Vanilla()
    {
        VersionedJar("server.jar", "1.21.1");

        var found = Detect();

        Assert.Equal(ServerType.Vanilla, found.Type);
        Assert.Equal("1.21.1", found.GameVersion);
        Assert.Equal("server.jar", found.JarFile);
        Assert.True(found.IsComplete);
    }

    [Fact]
    public void VanillaUnderItsOldName()
    {
        // The official download used to be called this, and plenty of old worlds still run on it.
        VersionedJar("minecraft_server.1.12.2.jar", "1.12.2");

        var found = Detect();

        Assert.Equal(ServerType.Vanilla, found.Type);
        Assert.Equal("minecraft_server.1.12.2.jar", found.JarFile);
    }

    [Fact]
    public void Paper()
    {
        VersionedJar("paper-1.21.1-130.jar", "1.21.1");

        var found = Detect();

        Assert.Equal(ServerType.Paper, found.Type);
        Assert.Equal("paper-1.21.1-130.jar", found.JarFile);
        Assert.Equal("1.21.1", found.GameVersion);
    }

    [Fact]
    public void Purpur()
    {
        // The name this app gives the jar when it creates a Purpur server. It matched no pattern at
        // all before, so an app-made Purpur server came back as "Vanilla, version unknown".
        VersionedJar("purpur-server.jar", "1.21.1");

        var found = Detect();

        Assert.Equal(ServerType.Purpur, found.Type);
        Assert.Equal("purpur-server.jar", found.JarFile);
        Assert.Equal("1.21.1", found.GameVersion);
    }

    [Fact]
    public void Fabric()
    {
        Jar("fabric-server.jar", "install.properties", "fabric-loader-version=0.16.2\ngame-version=1.21.1\n");

        var found = Detect();

        Assert.Equal(ServerType.Fabric, found.Type);
        Assert.Equal("fabric-server.jar", found.JarFile);
        Assert.Equal("1.21.1", found.GameVersion);
        Assert.Equal("0.16.2", found.LoaderVersion);
    }

    [Fact]
    public void FabricFromItsWebsite()
    {
        // The name fabricmc.net hands out, which this app never used.
        Jar("fabric-server-mc.1.21.1-loader.0.16.2-launcher.1.0.1.jar", "install.properties",
            "fabric-loader-version=0.16.2\ngame-version=1.21.1\n");

        var found = Detect();

        Assert.Equal(ServerType.Fabric, found.Type);
        Assert.Equal("fabric-server-mc.1.21.1-loader.0.16.2-launcher.1.0.1.jar", found.JarFile);
        Assert.Equal("1.21.1", found.GameVersion);
    }

    [Fact]
    public void FabricFromTheOldInstaller()
    {
        // No install.properties in this one: it runs the vanilla server.jar beside it, so that is
        // where the Minecraft version comes from.
        Jar("fabric-server-launch.jar", "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n");
        VersionedJar("server.jar", "1.20.1");

        var found = Detect();

        Assert.Equal(ServerType.Fabric, found.Type);
        Assert.Equal("fabric-server-launch.jar", found.JarFile);
        Assert.Equal("1.20.1", found.GameVersion);
    }

    [Fact]
    public void ModernForge()
    {
        File(Path.Combine("libraries", "net", "minecraftforge", "forge", "1.20.1-47.2.0", "win_args.txt"), "");

        var found = Detect();

        Assert.Equal(ServerType.Forge, found.Type);
        Assert.Equal("1.20.1-47.2.0", found.ForgeArgs);
        Assert.Equal("1.20.1", found.GameVersion);
        Assert.Equal("47.2.0", found.LoaderVersion);
        Assert.True(found.IsComplete);   // launched through the args file, so no jar is needed
    }

    [Theory]
    [InlineData("forge-1.12.2-14.23.5.2860.jar")]
    [InlineData("forge-1.12.2-14.23.5.2860-universal.jar")]
    public void OldForge(string jar)
    {
        File(jar, "");
        File("forge-1.12.2-14.23.5.2860-installer.jar", "");   // never the one to run

        var found = Detect();

        Assert.Equal(ServerType.Forge, found.Type);
        Assert.Equal(jar, found.JarFile);
        Assert.Equal("1.12.2", found.GameVersion);
        Assert.Equal("14.23.5.2860", found.LoaderVersion);
    }

    [Fact]
    public void NeoForge()
    {
        File(Path.Combine("libraries", "net", "neoforged", "neoforge", "21.1.77", "unix_args.txt"), "");

        var found = Detect();

        Assert.Equal(ServerType.NeoForge, found.Type);
        Assert.Equal("21.1.77", found.ForgeArgs);
        Assert.Equal("1.21.1", found.GameVersion);
    }

    // --- How it was set up ---

    [Fact]
    public void ThePortComesFromServerProperties()
    {
        File("server.properties", "motd=hola\nserver-port=25570\n");

        Assert.Equal(25570, Detect().Port);
    }

    [Fact]
    public void MemoryComesFromUserJvmArgs()
    {
        // Forge writes a user_jvm_args.txt that is mostly comments, including an example -Xmx4G
        // that nobody chose. Reading that would have every Forge server claim 4 GB.
        File("user_jvm_args.txt", "# Xmx and Xms set the maximum and minimum RAM usage.\n# -Xmx4G\n-Xms3G\n-Xmx6G\n");

        var found = Detect();

        Assert.Equal(3, found.MinRamGb);
        Assert.Equal(6, found.MaxRamGb);
    }

    [Fact]
    public void MemoryComesFromRunBatInMegabytes()
    {
        File("run.bat", "@echo off\r\nREM -Xmx1G is too little\r\njava -Xms2048M -Xmx6144M -jar server.jar nogui\r\npause\r\n");

        var found = Detect();

        Assert.Equal(2, found.MinRamGb);
        Assert.Equal(6, found.MaxRamGb);
    }

    [Fact]
    public void AWorldIsNoticed()
    {
        File(Path.Combine("world", "level.dat"), "");

        Assert.True(Detect().HasWorld);
    }

    // --- When it is not a server, or not all of one ---

    [Fact]
    public void AnUnknownFolderIsIncompleteAndListsItsJars()
    {
        // A modpack's own launcher, say: nothing recognisable, but the user knows which to run.
        File("modpack-launcher.jar", "");
        File("some-installer.jar", "");

        var found = Detect();

        Assert.Null(found.Type);
        Assert.False(found.IsComplete);
        Assert.Equal(new[] { "modpack-launcher.jar" }, found.Jars);
    }

    [Fact]
    public void AnEmptyOrMissingFolderIsNothingAndNeverThrows()
    {
        Assert.False(Detect().IsComplete);
        Assert.Same(ServerDetection.Nothing, new ServerDetectionService().Detect(Path.Combine(_folder, "no-existe")));
        Assert.Same(ServerDetection.Nothing, new ServerDetectionService().Detect(""));
    }

    [Fact]
    public void ARottenJarIsNotAVersion()
    {
        File("server.jar", "esto no es un zip");

        var found = Detect();

        Assert.Null(found.Type);
        Assert.Equal(new[] { "server.jar" }, found.Jars);
    }

    // --- Filling in an old config ---

    [Fact]
    public void FillingAnOldConfigStillWorks()
    {
        VersionedJar("purpur-server.jar", "1.21.1");
        var config = new ServerConfig { Name = "viejo", FolderPath = _folder };

        Assert.True(new ServerDetectionService().DetectAndFill(config));

        Assert.Equal(ServerType.Purpur, config.Type);
        Assert.Equal("1.21.1", config.GameVersion);
        Assert.Equal("purpur-server.jar", config.JarFile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
