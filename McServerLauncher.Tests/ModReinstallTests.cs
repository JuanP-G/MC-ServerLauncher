using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// Installing a mod that is already there under another version replaces it.
/// </summary>
/// <remarks>
/// The store only avoided a duplicate when the file name matched. Installing Sodium 0.6 over Sodium
/// 0.5 left both jars, and the loader then refused to start with "duplicate mod".
/// </remarks>
public class ModReinstallTests
{
    private static string Mods(string name) => Path.Combine(Path.GetTempPath(), "mods", name);

    [Fact]
    public void AnotherVersionOfTheSameProjectIsReplaced()
    {
        var installed = new Dictionary<string, string>
        {
            [Mods("sodium-0.5.jar")] = "AANobbMI",
            [Mods("lithium.jar")] = "gvQqBUqZ",
        };

        Assert.Equal(new[] { Mods("sodium-0.5.jar") },
            ServerModsViewModel.ReplacedBy(installed, "AANobbMI", Mods("sodium-0.6.jar")));
    }

    [Fact]
    public void ADisabledCopyIsReplacedToo()
    {
        var installed = new Dictionary<string, string> { [Mods("sodium-0.5.jar.disabled")] = "AANobbMI" };

        Assert.Single(ServerModsViewModel.ReplacedBy(installed, "AANobbMI", Mods("sodium-0.6.jar")));
    }

    [Fact]
    public void TheSameFileIsNotItsOwnReplacement()
    {
        // Same name: the download overwrites it in place, so deleting it afterwards would delete the
        // new one.
        var installed = new Dictionary<string, string> { [Mods("sodium-0.6.jar")] = "AANobbMI" };

        Assert.Empty(ServerModsViewModel.ReplacedBy(installed, "AANobbMI", Mods("sodium-0.6.jar")));
    }

    [Fact]
    public void OtherProjectsAreLeftAlone()
    {
        var installed = new Dictionary<string, string> { [Mods("lithium.jar")] = "gvQqBUqZ" };

        Assert.Empty(ServerModsViewModel.ReplacedBy(installed, "AANobbMI", Mods("sodium-0.6.jar")));
    }
}
