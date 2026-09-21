using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Taking over a folder that already holds a server, from the create dialog.
/// </summary>
/// <remarks>
/// This replaced the separate "Add" button. What has to hold, above everything else, is that adding
/// a server someone already has never turns into reinstalling it over itself: nothing downloaded,
/// nothing installed, nothing written — except the port, and only when it was changed.
/// </remarks>
public class ExistingServerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-existing-" + Guid.NewGuid().ToString("N"));

    public ExistingServerTests() => Directory.CreateDirectory(_folder);

    /// <summary>A Fabric server as this app would have made it, with a world and a properties file.</summary>
    private void AFabricServer()
    {
        using (var zip = ZipFile.Open(Path.Combine(_folder, "fabric-server.jar"), ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("install.properties").Open()))
            w.Write("fabric-loader-version=0.16.2\ngame-version=1.21.1\n");
        File.WriteAllText(Path.Combine(_folder, "server.properties"), "#Minecraft server properties\nmotd=hola\nserver-port=25570\n");
        File.WriteAllText(Path.Combine(_folder, "eula.txt"), "eula=true\n");
        File.WriteAllText(Path.Combine(_folder, "run.bat"), "java -Xms2G -Xmx5G -jar fabric-server.jar nogui\r\n");
        Directory.CreateDirectory(Path.Combine(_folder, "world"));
        File.WriteAllText(Path.Combine(_folder, "world", "level.dat"), "not nbt");
        Directory.CreateDirectory(Path.Combine(_folder, "mods"));
        File.WriteAllText(Path.Combine(_folder, "mods", "sodium.jar"), "");
    }

    private ExistingServerForm Form(string name = "survival", ServerType type = ServerType.Vanilla,
        string? version = null, string? jar = null, int min = 2, int max = 5, bool crossplay = false) =>
        new(name, _folder, type, version, jar, min, max, "java", Playit: true, Crossplay: crossplay,
            MultiVersion: false, Hydraulic: false);

    /// <summary>Every file in the folder and a hash of its bytes, to compare before and after.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_folder, f),
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    // --- The folder is left alone ---

    [Fact]
    public void AddingAServerChangesNothingInItsFolder()
    {
        AFabricServer();
        var before = Snapshot();

        var found = new ServerDetectionService().Detect(_folder);
        Assert.Null(ExistingServer.Problem(Form(), found, Array.Empty<string>()));
        Assert.False(ExistingServer.ApplyPort(_folder, found.Port, found.Port!.Value));
        ExistingServer.ToConfig(Form(), found);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void AChangedPortIsTheOnlyThingWritten()
    {
        AFabricServer();
        var before = Snapshot();

        Assert.True(ExistingServer.ApplyPort(_folder, 25570, 25600));

        var after = Snapshot();
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));   // no file added or removed
        foreach (var file in before.Keys.Where(k => k != "server.properties"))
            Assert.Equal(before[file], after[file]);

        // And only that key: the rest of the file, its comment and its order, as they were.
        var lines = File.ReadAllLines(Path.Combine(_folder, "server.properties"));
        Assert.Equal(new[] { "#Minecraft server properties", "motd=hola", "server-port=25600" }, lines);
    }

    // --- What ends up in the config ---

    [Fact]
    public void WhatTheFolderSaysWinsOverTheForm()
    {
        AFabricServer();
        var found = new ServerDetectionService().Detect(_folder);

        // The form still has the dialog's defaults — Vanilla, the latest release — which the
        // detection overrides; the picker is locked on screen for exactly this reason.
        var config = ExistingServer.ToConfig(Form(type: ServerType.Vanilla, version: "1.21.4"), found);

        Assert.Equal(ServerType.Fabric, config.Type);
        Assert.Equal("1.21.1", config.GameVersion);
        Assert.Equal("0.16.2", config.ModLoaderVersion);
        Assert.Equal("fabric-server.jar", config.JarFile);
        Assert.Equal(_folder, config.FolderPath);
        Assert.Equal("survival", config.Name);
        Assert.Equal(5, config.MaxRamGb);
        Assert.True(config.PlayitEnabled);
    }

    [Fact]
    public void WhatTheFolderCouldNotSayComesFromTheForm()
    {
        File.WriteAllText(Path.Combine(_folder, "modpack-launcher.jar"), "");   // nothing recognisable
        var found = new ServerDetectionService().Detect(_folder);
        Assert.Null(found.Type);

        var form = Form(type: ServerType.Paper, version: "1.20.4", jar: "modpack-launcher.jar");
        Assert.Null(ExistingServer.Problem(form, found, Array.Empty<string>()));
        var config = ExistingServer.ToConfig(form, found);

        Assert.Equal(ServerType.Paper, config.Type);
        Assert.Equal("1.20.4", config.GameVersion);
        Assert.Equal("modpack-launcher.jar", config.JarFile);
    }

    [Fact]
    public void AnOptionTheTypeCannotDoIsNotSaved()
    {
        // Forge has no Geyser; the checkbox is greyed out, and the config must agree with it.
        Directory.CreateDirectory(Path.Combine(_folder, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.2.0"));
        File.WriteAllText(Path.Combine(_folder, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.2.0", "win_args.txt"), "");
        var found = new ServerDetectionService().Detect(_folder);

        var config = ExistingServer.ToConfig(Form(crossplay: true), found);

        Assert.Equal(ServerType.Forge, config.Type);
        Assert.False(config.CrossplayEnabled);
        Assert.Equal("1.20.1-47.2.0", config.ForgeArgs);
    }

    // --- What is refused ---

    [Fact]
    public void AFolderAlreadyInTheListIsRefused()
    {
        AFabricServer();
        var found = new ServerDetectionService().Detect(_folder);

        // However it was typed: a trailing slash, or different case on Windows.
        var registered = new[] { _folder + Path.DirectorySeparatorChar };

        Assert.Equal("Cs_ExistingAlreadyAdded", ExistingServer.Problem(Form(), found, registered));
    }

    [Fact]
    public void AFolderThatIsNotThereIsRefused()
    {
        var form = Form() with { Folder = Path.Combine(_folder, "no-existe") };

        Assert.Equal("Cs_ExistingFolderMissing", ExistingServer.Problem(form, ServerDetection.Nothing, Array.Empty<string>()));
    }

    [Fact]
    public void WithNothingToLaunchItIsRefused()
    {
        var found = new ServerDetectionService().Detect(_folder);   // an empty folder

        Assert.Equal("Cs_ExistingNeedsJar",
            ExistingServer.Problem(Form(version: "1.21.1"), found, Array.Empty<string>()));
        Assert.Equal("Cs_ExistingNeedsJar",
            ExistingServer.Problem(Form(version: "1.21.1", jar: "no-esta.jar"), found, Array.Empty<string>()));
    }

    [Fact]
    public void WithoutAVersionItIsRefused()
    {
        File.WriteAllText(Path.Combine(_folder, "server.jar"), "");

        Assert.Equal("Cs_ExistingNeedsVersion",
            ExistingServer.Problem(Form(jar: "server.jar"), ServerDetection.Nothing, Array.Empty<string>()));
    }

    /// <summary>
    /// Every reason <see cref="ExistingServer.Problem"/> can give is a real string in every language.
    /// </summary>
    /// <remarks>
    /// It hands back keys rather than calling <c>Localizer.Get</c> itself, so the localization test
    /// that scans the code for keys cannot see them. A missing one would show the user the key.
    /// </remarks>
    [Fact]
    public void EveryReasonItGivesIsTranslated()
    {
        var source = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Services", "ExistingServer.cs"));
        var keys = Regex.Matches(source, @"return ""([A-Za-z0-9_]+)"";").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(keys);

        foreach (var file in new[] { "Strings.resx", "Strings.en.resx", "Strings.pt.resx", "Strings.fr.resx", "Strings.de.resx" })
        {
            var names = XDocument.Load(Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher", "Resources", file))
                .Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
            foreach (var key in keys)
                Assert.True(names.Contains(key), $"{file} no tiene «{key}»");
        }
    }

    [Theory]
    [InlineData(@"C:\servers\survival", @"C:\servers\survival\", true)]
    [InlineData(@"C:\servers\survival", @"C:\servers\creative", false)]
    [InlineData("", @"C:\servers\survival", false)]
    public void TheSameFolderIsRecognisedHoweverItIsTyped(string a, string b, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;   // the paths above are Windows paths
        Assert.Equal(expected, ExistingServer.SameFolder(a, b));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }
}
