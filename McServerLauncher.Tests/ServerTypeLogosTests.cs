using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>Every type in the picker shows its project's logo.</summary>
[Collection("avalonia")]
public class ServerTypeLogosTests(AvaloniaFixture ui)
{
    [Fact]
    public void EveryTypeOfTheCatalogHasALogo()
    {
        var missing = ServerTypeCatalog.All.Select(e => e.Type).Except(ServerTypeLogos.WithLogo).ToList();
        Assert.True(missing.Count == 0,
            "No logo for: " + string.Join(", ", missing) + ". Add its PNG under Resources/TypeLogos, a row in ServerTypeLogos and its source in NOTICE.");
    }

    [Fact]
    public void EveryLogoLoadsFromTheAssembly()
    {
        ui.Run(() =>
        {
            foreach (var type in ServerTypeLogos.WithLogo)
                Assert.NotNull(ServerTypeLogos.For(type));
        });
    }

    [Fact]
    public void EveryLogoIsCitedInNotice()
    {
        var notice = File.ReadAllText(Path.Combine(RepoRoot(), "NOTICE"));
        foreach (var entry in ServerTypeCatalog.All)
            Assert.Contains(entry.DisplayName, notice);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NOTICE"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("NOTICE not found above the test folder");
    }
}
