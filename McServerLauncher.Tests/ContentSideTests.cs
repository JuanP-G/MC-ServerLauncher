using System.IO.Compression;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What a jar says about the side it runs on.
/// </summary>
/// <remarks>
/// <para>
/// Read so the export can leave out of a player's pack the mods that only ever do anything on the
/// server. Geyser, Floodgate, a permissions plugin, a world-backup mod: megabytes the player
/// downloads, puts in their mods folder, and at best wastes memory on.
/// </para>
/// <para>
/// The jar is the only source here. What the store says about a project is the other half of the
/// decision and lives in its own test — this one is about reading the file correctly, which is
/// where a mistake would be invisible: a misread side does not fail, it silently excludes.
/// </para>
/// </remarks>
public class ContentSideTests : IDisposable
{
    private readonly List<string> _made = new();

    public void Dispose()
    {
        foreach (var path in _made)
            try { File.Delete(path); } catch { /* a temp file that outlives the run is harmless */ }
    }

    /// <summary>A real jar carrying one metadata file, at the path the loaders look for it.</summary>
    private string Jar(string entryName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcl-side-" + Guid.NewGuid().ToString("N") + ".jar");
        _made.Add(path);

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            manifest.Write("Manifest-Version: 1.0\n");
        using (var entry = new StreamWriter(zip.CreateEntry(entryName).Open()))
            entry.Write(content);

        return path;
    }

    private ContentManifest.ContentSide SideOfFabric(string environment) =>
        ContentManifest.Read(Jar("fabric.mod.json",
            $$"""{"schemaVersion":1,"id":"prueba","version":"1.0.0"{{environment}}}""")).Side;

    // --- Fabric ---

    [Fact]
    public void FabricSaysServer() =>
        Assert.Equal(ContentManifest.ContentSide.Server, SideOfFabric(",\"environment\":\"server\""));

    [Fact]
    public void FabricSaysClient() =>
        Assert.Equal(ContentManifest.ContentSide.Client, SideOfFabric(",\"environment\":\"client\""));

    [Fact]
    public void FabricStarIsReadAsBothAndAnAbsentKeyAsSilence()
    {
        // Read faithfully, and worth no more than each other. Measured against a real modpack,
        // environment:"*" carries no information: eleven jars out of eleven declared it, Floodgate
        // among them, which is a Bedrock authentication plugin with nothing to do on a client. What
        // the file says is recorded here; what it is worth is ExportSelection's business, and there
        // the two are treated identically.
        Assert.Equal(ContentManifest.ContentSide.Both, SideOfFabric(",\"environment\":\"*\""));
        Assert.Equal(ContentManifest.ContentSide.Unspecified, SideOfFabric(""));
    }

    [Fact]
    public void AnEnvironmentNobodyRecognisesIsReadAsSilence() =>
        Assert.Equal(ContentManifest.ContentSide.Unspecified, SideOfFabric(",\"environment\":\"vapour\""));

    // --- Forge and NeoForge ---

    [Fact]
    public void NeoForgeSaysItsSide()
    {
        var jar = Jar("META-INF/neoforge.mods.toml", """
            modLoader = "javafml"
            [[mods]]
            modId = "prueba"
            side = "SERVER"
            """);

        Assert.Equal(ContentManifest.ContentSide.Server, ContentManifest.Read(jar).Side);
    }

    [Fact]
    public void ASideInsideADependencyIsNotTheModsOwnSide()
    {
        // "side" is legal in two tables and means two different things. In [[dependencies.x]] it
        // says which side that dependency is needed on — so a perfectly ordinary client mod that
        // happens to need a server-side library would be filed as server-side and dropped from
        // every pack, which is precisely the crash this feature is supposed to prevent.
        var jar = Jar("META-INF/mods.toml", """
            modLoader = "javafml"
            [[mods]]
            modId = "prueba"
            [[dependencies.prueba]]
            modId = "unalibreria"
            side = "SERVER"
            """);

        Assert.Equal(ContentManifest.ContentSide.Unspecified, ContentManifest.Read(jar).Side);
        Assert.Contains("unalibreria", ContentManifest.Read(jar).Requires);
    }

    [Fact]
    public void DisplayTestIsNotReadAsASide()
    {
        // IGNORE_ALL_VERSION correlates with server-only mods and does not mean it: it controls the
        // version handshake between client and server, not where the mod loads. Acting on a
        // correlation here would leave real client mods out of packs with nothing to explain it.
        var jar = Jar("META-INF/mods.toml", """
            modLoader = "javafml"
            [[mods]]
            modId = "prueba"
            displayTest = "IGNORE_ALL_VERSION"
            """);

        Assert.Equal(ContentManifest.ContentSide.Unspecified, ContentManifest.Read(jar).Side);
    }

    [Fact]
    public void TheSideSurvivesATrailingComment()
    {
        // Generated mods.toml files are full of them, and the shared reader already strips them —
        // worth one test that the new key goes through the same door as the old ones.
        var jar = Jar("META-INF/mods.toml", """
            modLoader = "javafml"
            [[mods]]
            modId = "prueba"
            side = "CLIENT" # solo cliente
            """);

        Assert.Equal(ContentManifest.ContentSide.Client, ContentManifest.Read(jar).Side);
    }

    // --- Bukkit ---

    [Fact]
    public void APluginIsServerSideByDefinition()
    {
        // No parsing needed: a Bukkit plugin runs inside the server, and the platform has no client
        // half for it to run in.
        var jar = Jar("plugin.yml", "name: Essentials\nversion: 2.20\nmain: com.ejemplo.Main\n");

        Assert.Equal(ContentManifest.ContentSide.Server, ContentManifest.Read(jar).Side);
    }

    // --- What nothing says ---

    [Fact]
    public void AJarWithNoMetadataSaysNothingAboutItsSide()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcl-side-" + Guid.NewGuid().ToString("N") + ".jar");
        _made.Add(path);
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            manifest.Write("Manifest-Version: 1.0\n");

        Assert.Equal(ContentManifest.ContentSide.Unspecified, ContentManifest.Read(path).Side);
    }

    [Fact]
    public void AnUnreadableJarSaysNothingRatherThanThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcl-side-" + Guid.NewGuid().ToString("N") + ".jar");
        _made.Add(path);
        File.WriteAllText(path, "esto no es un zip");

        Assert.Equal(ContentManifest.ContentSide.Unspecified, ContentManifest.Read(path).Side);
    }
}
