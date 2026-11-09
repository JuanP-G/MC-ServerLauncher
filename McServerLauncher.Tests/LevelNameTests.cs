using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What the world folder is called: one definition, used by the backups, the seed, the player
/// statistics and the "is there a world yet" check.
/// </summary>
public class LevelNameTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcsl-level-" + Path.GetRandomFileName());

    public LevelNameTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temporary folder */ }
    }

    private static Dictionary<string, string> Props(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void WithoutALevelNameTheWorldIsCalledWorld() =>
        Assert.Equal("world", ServerPropertiesService.GetLevelName(Props()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankLevelNameIsTheDefaultToo(string value) =>
        Assert.Equal("world", ServerPropertiesService.GetLevelName(Props(("level-name", value))));

    [Theory]
    [InlineData("survival", "survival")]
    [InlineData("  survival  ", "survival")]
    [InlineData("worlds/survival", "worlds/survival")]
    public void ALevelNameIsWhatTheFileSays(string value, string expected) =>
        Assert.Equal(expected, ServerPropertiesService.GetLevelName(Props(("level-name", value))));

    [Fact]
    public void TheKeyIsFoundInAnyCase() =>
        Assert.Equal("a", ServerPropertiesService.GetLevelName(Props(("LEVEL-NAME", "a"))));

    [Fact]
    public void EverythingThatLooksForTheWorldAgreesOnItsName()
    {
        File.WriteAllText(Path.Combine(_folder, "server.properties"), "level-name=Isla\r\n");
        var level = Path.Combine(_folder, "Isla");
        Directory.CreateDirectory(level);
        File.WriteAllBytes(Path.Combine(level, "level.dat"), new byte[] { 0 });

        Assert.True(ServerCreationService.WorldExists(_folder));
        Assert.Equal("Isla", new WorldBackupService().GetLevelName(new Models.ServerConfig
        {
            Name = "T",
            FolderPath = _folder
        }));
    }

    /// <summary>
    /// One question, one definition: the line that reads <c>level-name</c> had four copies, and the
    /// next place that needs the world's name must ask <see cref="ServerPropertiesService.GetLevelName"/>
    /// instead of writing a fifth.
    /// </summary>
    [Fact]
    public void OnlyServerPropertiesServiceReadsTheLevelNameKey()
    {
        var root = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && Path.GetFileName(f) != "ServerPropertiesService.cs")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(x => x.Text.Contains("\"level-name\"", StringComparison.Ordinal)
                        && !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(x => $"{x.File}:{x.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Estos sitios leen level-name por su cuenta, usa ServerPropertiesService.GetLevelName: "
            + string.Join(", ", offenders));
    }
}
