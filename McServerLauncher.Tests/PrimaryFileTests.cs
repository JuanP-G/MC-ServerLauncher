using McServerLauncher.Models.Modrinth;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// A jar counts as its store project only when it is that version's main file.
/// </summary>
/// <remarks>
/// The project is what decides that an installed jar is an old copy of a new one. A version can carry
/// more than one file — a module, a sources jar — and a module counted as the project would be
/// deleted by installing the core.
/// </remarks>
public class PrimaryFileTests
{
    private static VersionResult Version(params (string Sha1, bool Primary)[] files) => new()
    {
        ProjectId = "essentials",
        Files = files.Select(f => new VersionFile
        {
            Filename = f.Sha1 + ".jar", Primary = f.Primary, Hashes = new FileHashes { Sha1 = f.Sha1 },
        }).ToList(),
    };

    [Fact]
    public void TheMainFileIsTheProject() =>
        Assert.True(ServerModsViewModel.IsPrimaryFile(Version(("core", true), ("chat", false)), "CORE"));

    [Fact]
    public void AnotherFileOfTheSameVersionIsNot() =>
        Assert.False(ServerModsViewModel.IsPrimaryFile(Version(("core", true), ("chat", false)), "chat"));

    [Fact]
    public void AVersionWithOneFileIsThatFileWhateverItSays() =>
        Assert.True(ServerModsViewModel.IsPrimaryFile(Version(("only", false)), "only"));

    [Fact]
    public void WhenTheStoreDoesNotSayWhichFileMatchedTheVersionCounts() =>
        Assert.True(ServerModsViewModel.IsPrimaryFile(Version(("core", true), ("chat", false)), "unknown"));
}
