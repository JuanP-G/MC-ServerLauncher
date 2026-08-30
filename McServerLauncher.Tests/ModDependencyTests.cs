using McServerLauncher.Models.Modrinth;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The library mods that come with a mod, and the ones that must not.
/// </summary>
/// <remarks>
/// <para>
/// Written from the failure it exists to prevent: the Fabric loader refusing to start with
/// <em>"Explorify needs any version of fabric-api, which you do not have"</em> and <em>"Towns and
/// Towers needs cristallib 3.1.3 or later"</em>. Both were installed through the app, and the app
/// installed exactly what was asked for and nothing it needed.
/// </para>
/// <para>
/// The walk is tested against a table rather than against Modrinth, so what is being checked is the
/// decisions — which dependency types count, what already-installed means, what stops a cycle —
/// and not whether a particular mod published something today.
/// </para>
/// </remarks>
public class ModDependencyTests
{
    /// <summary>A published version of <paramref name="projectId"/> with the dependencies given.</summary>
    private static VersionResult Version(string projectId, params VersionDependency[] deps) =>
        new()
        {
            Id = projectId + "-v1",
            ProjectId = projectId,
            VersionNumber = "1.0.0",
            Files = { new VersionFile { Url = $"https://example/{projectId}.jar", Filename = $"{projectId}.jar", Primary = true } },
            Dependencies = deps.ToList()
        };

    private static VersionDependency Needs(string projectId, string type = "required") =>
        new() { ProjectId = projectId, DependencyType = type };

    /// <summary>A lookup over a fixed catalogue, standing in for Modrinth.</summary>
    private static Func<string?, string?, CancellationToken, Task<VersionResult?>> Catalogue(
        params VersionResult[] known)
    {
        var byProject = known.ToDictionary(v => v.ProjectId, StringComparer.OrdinalIgnoreCase);
        var byVersion = known.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);

        return (projectId, versionId, _) =>
        {
            if (versionId is { Length: > 0 })
                return Task.FromResult(byVersion.GetValueOrDefault(versionId));
            return Task.FromResult(projectId is { Length: > 0 } ? byProject.GetValueOrDefault(projectId) : null);
        };
    }

    private static Task<ModDependencyService.Plan> Walk(VersionResult root,
        Func<string?, string?, CancellationToken, Task<VersionResult?>> catalogue,
        params string[] installed) =>
        ModDependencyService.WalkAsync(new[] { root }, installed, catalogue);

    [Fact]
    public async Task RequiredDependenciesAreInstalledAndNothingElseIs()
    {
        // "embedded" is the one that matters here and is easy to get wrong: it means the dependency
        // is already inside the jar that was just downloaded. Installing it again produces two
        // copies of the same mod and a different refusal to start — worse than the original bug.
        var root = Version("explorify",
            Needs("fabric-api"),
            Needs("sodium", "optional"),
            Needs("optifine", "incompatible"),
            Needs("mixinextras", "embedded"));

        var plan = await Walk(root, Catalogue(
            Version("fabric-api"), Version("sodium"), Version("optifine"), Version("mixinextras")));

        Assert.Equal(new[] { "fabric-api" }, plan.Install.Select(i => i.ProjectId));
        Assert.Empty(plan.Unresolved);
    }

    [Fact]
    public async Task ADependencyOfADependencyIsFollowed()
    {
        // The exact shape of the reported failure: Towns and Towers needs cristallib, and cristallib
        // needs fabric-api. Stopping at the first level would install one of the two missing jars
        // and leave the loader complaining about the other.
        var root = Version("t_and_t", Needs("cristallib"));

        var plan = await Walk(root, Catalogue(
            Version("cristallib", Needs("fabric-api")),
            Version("fabric-api")));

        Assert.Equal(new[] { "cristallib", "fabric-api" }, plan.Install.Select(i => i.ProjectId));
    }

    [Fact]
    public async Task TwoModsNeedingTheSameLibraryInstallItOnce()
    {
        var root = Version("pack", Needs("mod-a"), Needs("mod-b"));

        var plan = await Walk(root, Catalogue(
            Version("mod-a", Needs("fabric-api")),
            Version("mod-b", Needs("fabric-api")),
            Version("fabric-api")));

        Assert.Single(plan.Install, i => i.ProjectId == "fabric-api");
    }

    [Fact]
    public async Task ACycleEndsInsteadOfLoopingForever()
    {
        // Two mods declaring each other is not hypothetical — it happens with split libraries — and
        // it is the one input that turns a graph walk into a hang.
        var root = Version("a", Needs("b"));

        var plan = await Walk(root, Catalogue(
            Version("b", Needs("a")),
            Version("a", Needs("b"))));

        Assert.Equal(new[] { "b" }, plan.Install.Select(i => i.ProjectId));
    }

    [Fact]
    public async Task WhatIsAlreadyInstalledIsLeftAlone()
    {
        // Modrinth's dependencies carry no version range: a dependency either pins one exact version
        // or names a project and takes whatever is current. So "the project is present" is a
        // complete answer, and downloading a second copy under another file name is how you get the
        // loader's "duplicate mod" error.
        var root = Version("explorify", Needs("fabric-api"));

        var plan = await Walk(root, Catalogue(Version("fabric-api")), installed: "fabric-api");

        Assert.Empty(plan.Install);
    }

    [Fact]
    public async Task TheModBeingInstalledIsNeverListedAsItsOwnDependency()
    {
        // A version that (wrongly, but it happens) lists its own project.
        var root = Version("explorify", Needs("explorify"), Needs("fabric-api"));

        var plan = await Walk(root, Catalogue(Version("explorify"), Version("fabric-api")));

        Assert.Equal(new[] { "fabric-api" }, plan.Install.Select(i => i.ProjectId));
    }

    [Fact]
    public async Task APinnedDependencyIsTakenByItsVersionIdNotTheNewestOne()
    {
        // When a dependency names a version instead of a project, that exact file is the answer;
        // resolving "the latest of that project" would install something the author did not ask for.
        var pinned = Version("cristallib");
        pinned.Id = "cristallib-3.1.3";
        pinned.VersionNumber = "3.1.3";

        var newest = Version("cristallib");
        newest.VersionNumber = "4.0.0";

        var root = Version("t_and_t");
        root.Dependencies.Add(new VersionDependency { VersionId = "cristallib-3.1.3", DependencyType = "required" });

        // The catalogue answers by project id with the newest; by version id with the pinned one.
        var byVersion = new Dictionary<string, VersionResult> { ["cristallib-3.1.3"] = pinned };
        var plan = await ModDependencyService.WalkAsync(new[] { root }, Array.Empty<string>(),
            (projectId, versionId, _) => Task.FromResult(
                versionId is { Length: > 0 } ? byVersion.GetValueOrDefault(versionId)
                : projectId == "cristallib" ? newest : null));

        var only = Assert.Single(plan.Install);
        Assert.Equal("3.1.3", only.Version.VersionNumber);
    }

    [Fact]
    public async Task ADependencyWithNoCompatibleBuildIsNamedRatherThanIgnored()
    {
        // Silence here would be the worst outcome: the mod installs, the app says it went well, and
        // the server refuses to start over a name the user never sees.
        var root = Version("explorify", Needs("some-library"));

        var plan = await Walk(root, Catalogue(/* nothing resolves */));

        Assert.Empty(plan.Install);
        Assert.Equal(new[] { "some-library" }, plan.Unresolved);
    }

    [Fact]
    public async Task AnUnresolvableDependencyIsReportedOnceEvenIfSeveralModsNeedIt()
    {
        var root = Version("pack", Needs("mod-a"), Needs("mod-b"));

        var plan = await Walk(root, Catalogue(
            Version("mod-a", Needs("gone")),
            Version("mod-b", Needs("gone"))));

        Assert.Equal(new[] { "gone" }, plan.Unresolved);
    }

    [Fact]
    public async Task AVersionWithNoFileIsNotTreatedAsInstallable()
    {
        // A version with an empty file list would otherwise reach the downloader as a null URL.
        var empty = new VersionResult { Id = "x-v1", ProjectId = "x" };
        var root = Version("explorify", Needs("x"));

        var plan = await Walk(root, Catalogue(empty));

        Assert.Empty(plan.Install);
        Assert.Equal(new[] { "x" }, plan.Unresolved);
    }

    [Fact]
    public async Task TheWalkIsBoundedNoMatterWhatTheDataSays()
    {
        // A chain of a thousand: one click must not turn into an unbounded download because a
        // project's metadata is wrong or hostile.
        var known = Enumerable.Range(0, 1000)
            .Select(i => Version($"lib{i}", Needs($"lib{i + 1}")))
            .ToArray();
        var root = Version("root", Needs("lib0"));

        var plan = await Walk(root, Catalogue(known));

        Assert.InRange(plan.Install.Count, 1, 50);
    }

    // --- what the jar itself says ---
    //
    // Modrinth's dependency list is not always right, and this is not a hypothetical. Checked
    // against the live API on 2026-08-30: Explorify v1.6.5 for 1.21.1 declares NO dependencies on
    // Modrinth, and its own fabric.mod.json says {"fabric-api": "*", "minecraft": ">=1.20"} —
    // fabric-api being exactly what the loader refused to start without. Resolving only what
    // Modrinth publishes would have left that case as broken as before.

    /// <summary>A jar carrying the <c>fabric.mod.json</c> given, on disk.</summary>
    private static string JarWith(string? fabricModJson)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcl-jar-" + Guid.NewGuid().ToString("N") + ".jar");
        using var zip = new System.IO.Compression.ZipArchive(File.Create(path),
            System.IO.Compression.ZipArchiveMode.Create);

        // Something every jar has, so the "no metadata" case is a real jar and not an empty file.
        using (var manifest = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            manifest.Write("Manifest-Version: 1.0\n");

        if (fabricModJson is not null)
            using (var mod = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                mod.Write(fabricModJson);

        return path;
    }

    [Fact]
    public void TheJarSaysWhatItNeedsEvenWhenModrinthDoesNot()
    {
        // Explorify's metadata, verbatim.
        var jar = JarWith(
            """
            {"schemaVersion": 1, "id": "explorify",
             "depends": {"fabric-api": "*", "minecraft": ">=1.20"}}
            """);
        try
        {
            // minecraft is provided by the loader; asking Modrinth for it would be nonsense.
            Assert.Equal(new[] { "fabric-api" }, ModDependencyService.DeclaredModIds(jar));
        }
        finally { File.Delete(jar); }
    }

    [Fact]
    public void OnlyHardDependenciesCountNotTheAuthorsAdvice()
    {
        var jar = JarWith(
            """
            {"id": "x",
             "depends": {"cristallib": ">=3.1.3", "fabricloader": ">=0.15", "java": ">=17"},
             "recommends": {"sodium": "*"},
             "suggests": {"iris": "*"}}
            """);
        try
        {
            // recommends and suggests are suggestions. Installing them would fill the folder with
            // mods nobody chose, which is the thing this whole feature must not become.
            Assert.Equal(new[] { "cristallib" }, ModDependencyService.DeclaredModIds(jar));
        }
        finally { File.Delete(jar); }
    }

    [Fact]
    public void APluginJarDeclaresNothingAndThatIsNotAFailure()
    {
        // Paper and Purpur content has no fabric.mod.json at all, and the install path must not
        // treat its absence as anything but "nothing to add".
        var jar = JarWith(null);
        try { Assert.Empty(ModDependencyService.DeclaredModIds(jar)); }
        finally { File.Delete(jar); }
    }

    [Fact]
    public void UnreadableMetadataIsNotTreatedAsAMissingDependency()
    {
        // The jar is already downloaded and checksum-verified by the time this runs. Refusing it
        // over metadata that will not parse would break an install that was otherwise fine.
        var jar = JarWith("{ this is not json");
        try { Assert.Empty(ModDependencyService.DeclaredModIds(jar)); }
        finally { File.Delete(jar); }

        Assert.Empty(ModDependencyService.DeclaredModIds(
            Path.Combine(Path.GetTempPath(), "mcl-does-not-exist-" + Guid.NewGuid().ToString("N") + ".jar")));
    }

    [Fact]
    public async Task AModIdModrinthNeverHeardOfIsNotReportedAsMissing()
    {
        // A mod id is not a project id: cristallib is published as "cristel-lib" and does not
        // resolve by its id. Reporting that as missing would turn a guess into a warning about a
        // mod that may well be installed under another name.
        var plan = await ModDependencyService.WalkAsync(
            new[] { ModDependencyService.ModIdRoot(new[] { "cristallib" }) },
            Array.Empty<string>(),
            (_, _, _) => Task.FromResult<VersionResult?>(null));

        // The walk itself reports it, because for a Modrinth-declared dependency that is the truth...
        Assert.Equal(new[] { "cristallib" }, plan.Unresolved);

        // ...and the by-mod-id path drops it, because there it is only a guess that failed.
        Assert.Empty(ModDependencyService.WithoutUnresolved(plan).Unresolved);
        Assert.Equal(plan.Install, ModDependencyService.WithoutUnresolved(plan).Install);
    }

    [Fact]
    public void TheSyntheticRootCannotBeMistakenForAProject()
    {
        // It goes into the same "already settled" set as real project ids. One that could collide
        // with a real project would silently mark that project as installed.
        Assert.Contains(" ", ModDependencyService.ModIdRoot(new[] { "x" }).ProjectId);
        Assert.Empty(ModDependencyService.ModIdRoot(new[] { "  ", "" }).Dependencies);
        Assert.Single(ModDependencyService.ModIdRoot(new[] { "fabric-api", "Fabric-API" }).Dependencies);
    }
}
