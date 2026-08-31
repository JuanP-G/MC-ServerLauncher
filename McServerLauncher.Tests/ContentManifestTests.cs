using System.IO.Compression;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Reading what a mod or plugin jar says about itself, in the three formats that exist.
/// </summary>
/// <remarks>
/// Against real zip files rather than strings, because half of what can go wrong is about finding
/// the right entry inside the archive. The app only ever read <c>fabric.mod.json</c>, so a Paper
/// server's plugins and a NeoForge server's mods were invisible to it — which made "every mod and
/// plugin has its dependencies" a claim it could not actually check.
/// </remarks>
public class ContentManifestTests : IDisposable
{
    private readonly List<string> _made = new();

    public void Dispose()
    {
        foreach (var path in _made)
            try { File.Delete(path); } catch { /* a temp file that outlives the run is harmless */ }
    }

    /// <summary>A real jar carrying one metadata file, at the path the loaders look for it.</summary>
    private string Jar(string? entryName, string? content, string fileName = "test.jar")
    {
        var path = Path.Combine(Path.GetTempPath(),
            "mcl-cm-" + Guid.NewGuid().ToString("N") + "-" + fileName);
        _made.Add(path);

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        // Every jar has one, so "no metadata" is a real jar rather than an empty file.
        using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            manifest.Write("Manifest-Version: 1.0\n");

        if (entryName is not null && content is not null)
            using (var entry = new StreamWriter(zip.CreateEntry(entryName).Open()))
                entry.Write(content);

        return path;
    }

    // --- Fabric ---

    [Fact]
    public void AFabricModSaysWhatItIsAndWhatItNeeds()
    {
        var jar = Jar("fabric.mod.json",
            """
            {"schemaVersion": 1, "id": "explorify",
             "depends": {"fabric-api": "*", "minecraft": ">=1.20", "java": ">=17"}}
            """);

        var manifest = ContentManifest.Read(jar);

        Assert.Equal(new[] { "explorify" }, manifest.Provides);
        // minecraft and java come from the loader: nothing could ever be installed to satisfy them.
        Assert.Equal(new[] { "fabric-api" }, manifest.Requires);
    }

    [Fact]
    public void AModThatProvidesAnotherIdSatisfiesIt()
    {
        // A fork standing in for the original, or one jar carrying what used to be several. Missing
        // this invents a missing dependency for a server that is in fact complete.
        var provider = Jar("fabric.mod.json",
            """
            {"schemaVersion": 1, "id": "sodium-fork", "provides": ["sodium"]}
            """, "provider.jar");

        var needer = Jar("fabric.mod.json",
            """
            {"schemaVersion": 1, "id": "shader-thing", "depends": {"sodium": "*"}}
            """, "needer.jar");

        var missing = ContentDependencyCheck.Check(new[]
        {
            ContentManifest.Read(provider), ContentManifest.Read(needer)
        });

        Assert.Empty(missing);
    }

    [Fact]
    public void AdviceIsNotARequirement()
    {
        var jar = Jar("fabric.mod.json",
            """
            {"schemaVersion": 1, "id": "polite",
             "recommends": {"sodium": "*"}, "suggests": {"iris": "*"}}
            """);

        // Blocking a start over the author's suggestions is how a check becomes one people disable.
        Assert.Empty(ContentManifest.Read(jar).Requires);
    }

    // --- Bukkit, Paper, Purpur ---

    [Fact]
    public void APluginYmlIsReadAtAll()
    {
        // The whole gap: before this, a Paper server's plugins declared nothing the app could see.
        var jar = Jar("plugin.yml",
            """
            name: MyPlugin
            version: 1.2.3
            main: com.example.MyPlugin
            depend: [Vault, ProtocolLib]
            softdepend: [PlaceholderAPI]
            """);

        var manifest = ContentManifest.Read(jar);

        Assert.Equal(new[] { "MyPlugin" }, manifest.Provides);
        Assert.Equal(new[] { "Vault", "ProtocolLib" }, manifest.Requires);
    }

    [Fact]
    public void ASoftDependDoesNotBlock()
    {
        var jar = Jar("plugin.yml",
            """
            name: Solo
            softdepend: [PlaceholderAPI, Vault]
            """);

        // softdepend only asks to be loaded after something if it happens to be there. Treating it
        // as a requirement would stop starts over plugins nobody ever intended to install.
        Assert.Empty(ContentManifest.Read(jar).Requires);
    }

    [Fact]
    public void ADependWrittenAsAListOfDashesIsReadToo()
    {
        // Both spellings are equally common in real files; reading only one would miss half the
        // plugins for a reason no user could ever guess.
        var jar = Jar("plugin.yml",
            """
            name: Blocky
            depend:
              - Vault
              - "WorldEdit"
            main: com.example.Blocky
            """);

        Assert.Equal(new[] { "Vault", "WorldEdit" }, ContentManifest.Read(jar).Requires);
    }

    [Fact]
    public void ACommandNamedInsideThePluginIsNotThePluginsName()
    {
        // "name" appears again under commands: and permissions:. Taking the first match anywhere in
        // the file reads a command's name as the plugin's, and then nothing that depends on the
        // plugin is ever satisfied.
        var jar = Jar("plugin.yml",
            """
            name: RealName
            commands:
              fly:
                name: NotTheName
                description: fly around
            """);

        Assert.Equal(new[] { "RealName" }, ContentManifest.Read(jar).Provides);
    }

    [Fact]
    public void APaperPluginYmlWorksTheSameWay()
    {
        var jar = Jar("paper-plugin.yml",
            """
            name: Modern
            depend: [Vault]
            """);

        Assert.Equal(new[] { "Modern" }, ContentManifest.Read(jar).Provides);
        Assert.Equal(new[] { "Vault" }, ContentManifest.Read(jar).Requires);
    }

    // --- Forge and NeoForge ---

    [Fact]
    public void ANeoForgeModIsRead()
    {
        var jar = Jar("META-INF/neoforge.mods.toml",
            """
            modLoader = "javafml"
            loaderVersion = "[1,)"

            [[mods]]
            modId = "citadel"
            version = "2.6.0"

            [[dependencies.citadel]]
            modId = "neoforge"
            type = "required"

            [[dependencies.citadel]]
            modId = "jei"
            type = "required"

            [[dependencies.citadel]]
            modId = "curios"
            type = "optional"
            """);

        var manifest = ContentManifest.Read(jar);

        Assert.Equal(new[] { "citadel" }, manifest.Provides);
        // neoforge is the loader; curios is optional. Only jei is a real requirement.
        Assert.Equal(new[] { "jei" }, manifest.Requires);
    }

    [Fact]
    public void TheOlderForgeSpellingIsReadToo()
    {
        // Forge wrote mandatory = true where NeoForge writes type = "required". Both are in the
        // wild, and the same code has to read a jar built for either.
        var jar = Jar("META-INF/mods.toml",
            """
            [[mods]]
            modId = "oldmod"

            [[dependencies.oldmod]]
            modId = "patchouli"
            mandatory = true

            [[dependencies.oldmod]]
            modId = "jade"
            mandatory = false
            """);

        Assert.Equal(new[] { "patchouli" }, ContentManifest.Read(jar).Requires);
    }

    [Fact]
    public void ATrailingCommentDoesNotBecomePartOfTheName()
    {
        var jar = Jar("META-INF/mods.toml",
            """
            [[mods]]
            modId = "commented" # el id del mod

            [[dependencies.commented]]
            modId = "jei" # hace falta
            type = "required"
            """);

        var manifest = ContentManifest.Read(jar);

        Assert.Equal(new[] { "commented" }, manifest.Provides);
        Assert.Equal(new[] { "jei" }, manifest.Requires);
    }

    // --- Nothing to say ---

    [Fact]
    public void AJarWithNoMetadataDeclaresNothingAndThatIsNotAFailure()
    {
        var manifest = ContentManifest.Read(Jar(null, null));

        Assert.True(manifest.IsSilent);
        Assert.Empty(manifest.Requires);
    }

    [Fact]
    public void AJarThatCannotBeOpenedDeclaresNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcl-cm-broken-" + Guid.NewGuid().ToString("N") + ".jar");
        _made.Add(path);
        File.WriteAllText(path, "esto no es un zip");

        // A corrupt jar is the loader's problem to report. Throwing here would stop the server from
        // starting over a file the app was only inspecting.
        Assert.True(ContentManifest.Read(path).IsSilent);
    }

    [Fact]
    public void BrokenMetadataDeclaresNothing()
    {
        Assert.True(ContentManifest.Read(Jar("fabric.mod.json", "{ esto no es json")).IsSilent);
    }

    [Fact]
    public void AJarThatDependsOnItselfIsNotWaitingForAnything()
    {
        var jar = Jar("fabric.mod.json",
            """
            {"schemaVersion": 1, "id": "narcissus", "depends": {"narcissus": "*"}}
            """);

        Assert.Empty(ContentManifest.Read(jar).Requires);
    }

    // --- The check over a whole folder ---

    [Fact]
    public void WhatIsMissingIsNamedWithWhoNeedsIt()
    {
        var a = Jar("fabric.mod.json", """{"schemaVersion":1,"id":"a","depends":{"fabric-api":"*"}}""", "a.jar");
        var b = Jar("fabric.mod.json", """{"schemaVersion":1,"id":"b","depends":{"fabric-api":"*"}}""", "b.jar");

        var missing = ContentDependencyCheck.Check(new[]
        {
            ContentManifest.Read(a), ContentManifest.Read(b)
        });

        // One thing to install, not two: grouped by what is missing, so a short problem does not
        // read as a long one.
        var only = Assert.Single(missing);
        Assert.Equal("fabric-api", only.Id);
        Assert.Equal(2, only.NeededBy.Count);
    }

    [Fact]
    public void NothingIsMissingWhenTheDependencyIsInstalled()
    {
        var api = Jar("fabric.mod.json", """{"schemaVersion":1,"id":"fabric-api"}""", "api.jar");
        var mod = Jar("fabric.mod.json", """{"schemaVersion":1,"id":"a","depends":{"fabric-api":"*"}}""", "a.jar");

        Assert.Empty(ContentDependencyCheck.Check(new[]
        {
            ContentManifest.Read(api), ContentManifest.Read(mod)
        }));
    }

    [Fact]
    public void TheNameIsMatchedWithoutCaringAboutCase()
    {
        // plugin.yml names are written however the author felt that day, and "vault" not matching
        // "Vault" would report a missing plugin that is sitting right there in the folder.
        var vault = Jar("plugin.yml", "name: Vault\n", "vault.jar");
        var user = Jar("plugin.yml", "name: Shop\ndepend: [vault]\n", "shop.jar");

        Assert.Empty(ContentDependencyCheck.Check(new[]
        {
            ContentManifest.Read(vault), ContentManifest.Read(user)
        }));
    }

    [Fact]
    public void ADisabledJarIsNeitherInstalledNorMissing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcl-cm-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Jar("fabric.mod.json", """{"schemaVersion":1,"id":"a","depends":{"x":"*"}}"""),
                Path.Combine(folder, "a.jar.disabled"));

            // Counting a disabled jar would both invent a gap it cannot cause and hide one it used
            // to fill. Skipping it is the entire meaning of the extension.
            Assert.Empty(ContentManifest.ReadFolder(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnEmptyOrMissingFolderIsFine()
    {
        Assert.Empty(ContentManifest.ReadFolder(
            Path.Combine(Path.GetTempPath(), "mcl-cm-nope-" + Guid.NewGuid().ToString("N"))));
    }
}
