using System.IO.Compression;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Mods that carry other mods inside their own jar.
/// </summary>
/// <remarks>
/// <para>
/// The report, from a real server's console: the start check announced six missing dependencies —
/// <c>fabric-api-base</c>, three more fabric modules and <c>xaerolib</c> — said they had to be
/// installed by hand, and the loader then listed every one of them as loaded. They were inside
/// <c>fabric-api</c> and inside Xaero's minimap, and the check only ever read the outer manifest.
/// </para>
/// <para>
/// The jars below are built for real, a zip inside a zip, laid out where Fabric and Forge put them.
/// A test that faked the manifest would have passed against the old code too.
/// </para>
/// </remarks>
public class NestedJarTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "mcl-nested-" + Guid.NewGuid().ToString("N"));

    public NestedJarTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>The bytes of a Fabric jar with this id, these depends, and these jars inside it.</summary>
    private static byte[] FabricJar(string id, string[]? depends = null, params (string Path, byte[] Jar)[] nested)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var dependsJson = string.Join(",", (depends ?? Array.Empty<string>()).Select(d => $"\"{d}\":\"*\""));
            var jarsJson = string.Join(",", nested.Select(n => $"{{\"file\":\"{n.Path}\"}}"));

            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                writer.Write($"{{\"schemaVersion\":1,\"id\":\"{id}\",\"version\":\"1\"," +
                             $"\"depends\":{{{dependsJson}}},\"jars\":[{jarsJson}]}}");

            foreach (var (path, jar) in nested)
                using (var entry = zip.CreateEntry(path).Open())
                    entry.Write(jar, 0, jar.Length);
        }
        return buffer.ToArray();
    }

    /// <summary>A NeoForge jar with this mod id and these jars under META-INF/jarjar.</summary>
    private static byte[] NeoForgeJar(string modId, params (string Path, byte[] Jar)[] nested)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("META-INF/neoforge.mods.toml").Open()))
                writer.Write($"modLoader = \"javafml\"\n[[mods]]\nmodId = \"{modId}\"\n");

            foreach (var (path, jar) in nested)
                using (var entry = zip.CreateEntry(path).Open())
                    entry.Write(jar, 0, jar.Length);
        }
        return buffer.ToArray();
    }

    private string Save(string fileName, byte[] jar)
    {
        var path = Path.Combine(_folder, fileName);
        File.WriteAllBytes(path, jar);
        return path;
    }

    [Fact]
    public void AModThatCarriesItsOwnLibraryIsNotMissingIt()
    {
        // Xaero's minimap, exactly: it depends on xaerolib, and xaerolib is in its own jar.
        var minimap = Save("xaerominimap.jar", FabricJar("xaerominimap", new[] { "xaerolib" },
            ("META-INF/jars/xaerolib.jar", FabricJar("xaerolib"))));

        var manifest = ContentManifest.Read(minimap);

        Assert.Contains("xaerolib", manifest.Provides);
        Assert.DoesNotContain("xaerolib", manifest.Requires);
    }

    [Fact]
    public void FabricApisModulesCountAsInstalledForEveryOtherMod()
    {
        // The other half of the report: mods that depend on a single fabric module by name, which
        // fabric-api only ever ships nested. The check must see the module as installed.
        Save("fabric-api.jar", FabricJar("fabric-api", nested: new[]
        {
            ("META-INF/jars/fabric-api-base.jar", FabricJar("fabric-api-base")),
            ("META-INF/jars/fabric-lifecycle-events-v1.jar", FabricJar("fabric-lifecycle-events-v1")),
            ("META-INF/jars/fabric-networking-api-v1.jar", FabricJar("fabric-networking-api-v1"))
        }));
        Save("carryon.jar", FabricJar("carryon",
            new[] { "fabric-api-base", "fabric-lifecycle-events-v1", "fabric-networking-api-v1" }));

        var missing = ContentDependencyCheck.Check(ContentManifest.ReadFolder(_folder));

        Assert.Empty(missing);
    }

    [Fact]
    public void AGenuinelyMissingDependencyIsStillReported()
    {
        // The fix must not blind the check. Something nobody carries is still missing, and saying so
        // before the start is the whole reason the check exists.
        Save("fabric-api.jar", FabricJar("fabric-api", nested:
            ("META-INF/jars/fabric-api-base.jar", FabricJar("fabric-api-base"))));
        Save("explorify.jar", FabricJar("explorify", new[] { "fabric-api-base", "cloth-config" }));

        var missing = ContentDependencyCheck.Check(ContentManifest.ReadFolder(_folder));

        Assert.Equal("cloth-config", Assert.Single(missing).Id);
    }

    [Fact]
    public void NestingIsFollowedMoreThanOneLevelDown()
    {
        var deepest = FabricJar("mixinextras");
        var middle = FabricJar("libreria", nested: ("META-INF/jars/mixinextras.jar", deepest));
        var outer = Save("mod.jar", FabricJar("mod", nested: ("META-INF/jars/libreria.jar", middle)));

        var provides = ContentManifest.Read(outer).Provides;

        Assert.Contains("libreria", provides);
        Assert.Contains("mixinextras", provides);
    }

    [Fact]
    public void ANeoForgeJarJarCountsToo()
    {
        // Forge and NeoForge put theirs under META-INF/jarjar. Same idea, different drawer.
        var jar = Save("mod.jar", NeoForgeJar("mimod",
            ("META-INF/jarjar/libreria.jar", NeoForgeJar("libreria"))));

        Assert.Contains("libreria", ContentManifest.Read(jar).Provides);
    }

    [Fact]
    public void ABrokenJarInsideAJarIsIgnoredRatherThanFatal()
    {
        // Same rule as the outer jar: unreadable means it declares nothing, never that the check
        // throws and the server cannot be started over it.
        var jar = Save("mod.jar", FabricJar("mod", nested:
            ("META-INF/jars/roto.jar", "esto no es un zip"u8.ToArray())));

        var manifest = ContentManifest.Read(jar);

        Assert.Equal(new[] { "mod" }, manifest.Provides);
    }

    [Fact]
    public void WhatANestedJarRequiresIsNotAddedToTheOuterOne()
    {
        // The author ships the bundle as a unit. A gap inside it is the loader's to report; pulling
        // nested requirements in would invent missing dependencies, which is the bug being fixed.
        var jar = Save("mod.jar", FabricJar("mod", nested:
            ("META-INF/jars/libreria.jar", FabricJar("libreria", new[] { "algo-que-no-esta" }))));

        Assert.Empty(ContentManifest.Read(jar).Requires);
    }

    [Fact]
    public void MixinExtrasComesWithTheLoaderAndIsNeverMissing()
    {
        // Found by pointing the fixed check at a real folder: Lithium depends on mixinextras, which
        // Fabric Loader carries inside itself — the server's own log lists it under fabricloader.
        // It is never in mods/, so reading the folder can never find it, however deep it looks.
        Save("lithium.jar", FabricJar("lithium", new[] { "mixinextras" }));

        Assert.Empty(ContentDependencyCheck.Check(ContentManifest.ReadFolder(_folder)));
    }

    [Fact]
    public void TheModsTabDoesNotAskTheStoreForSomethingTheJarCarries()
    {
        // The second reader that had the same blind spot. It fed the Mods tab's "missing libraries"
        // scan and the dependency pull on install — so installing Xaero's minimap from the store
        // asked Modrinth for a second copy of the xaerolib already inside it.
        var minimap = Save("xaerominimap.jar", FabricJar("xaerominimap", new[] { "fabric-api", "xaerolib" },
            ("META-INF/jars/xaerolib.jar", FabricJar("xaerolib"))));

        Assert.Equal(new[] { "fabric-api" }, ModDependencyService.DeclaredModIds(minimap));
    }
}
