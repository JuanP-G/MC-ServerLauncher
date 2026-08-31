using System.Text.RegularExpressions;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The check that runs when you press Start, and the promises it has to keep.
/// </summary>
/// <remarks>
/// The app already had a missing-dependency panel, on the Mods tab. It did not help, because it is
/// on a tab nobody opens before pressing Start — the server fell over anyway. Moving the question to
/// the moment it matters is only worth doing if it never blocks an unattended start and never claims
/// everything is fine when it simply could not ask.
/// </remarks>
public class StartDependencyGateTests
{
    [Fact]
    public void AnAutomaticStartIsNeverInterrupted()
    {
        // Auto-restart after a crash and wake-on-demand both happen with nobody there. A modal
        // would leave the server down until somebody came back, which is what those two features
        // exist to prevent.
        Assert.False(ContentDependencyCheck.ShouldAsk(missingCount: 3, isAutoRestart: true));
    }

    [Fact]
    public void APersonPressingStartIsAsked()
    {
        Assert.True(ContentDependencyCheck.ShouldAsk(missingCount: 1, isAutoRestart: false));
    }

    [Fact]
    public void NothingMissingMeansNoQuestion()
    {
        Assert.False(ContentDependencyCheck.ShouldAsk(missingCount: 0, isAutoRestart: false));
        Assert.False(ContentDependencyCheck.ShouldAsk(missingCount: 0, isAutoRestart: true));
    }

    [Fact]
    public void TheCheckNeverTouchesTheNetwork()
    {
        // The point of reading the jars. The two store calls that could answer this swallow every
        // error and return an empty result, so a check built on them reports "nothing is missing"
        // whenever the connection is down — on the one screen where being wrong means the server
        // does not come up. This test is what stops that creeping back in.
        foreach (var file in new[] { "ContentManifest.cs", "ContentDependencyCheck.cs" })
        {
            var source = File.ReadAllText(Path.Combine(
                LocalizationTests.RepoRoot(), "McServerLauncher", "Services", file));

            foreach (var forbidden in new[] { "HttpClient", "ModrinthService", "System.Net" })
                Assert.False(source.Contains(forbidden, StringComparison.Ordinal),
                    $"{file} menciona «{forbidden}»: la comprobación de arranque tiene que responder sin red");
        }
    }

    [Fact]
    public void TheDialogOffersThreeWaysOut()
    {
        var xaml = DialogMarkup();

        // Two would be the trap: blocking outright is wrong the day the check is mistaken, and a
        // check that can trap you is one people learn to switch off.
        Assert.Contains("Deps_InstallAndStart", xaml);
        Assert.Contains("Deps_StartAnyway", xaml);
        Assert.Contains("Cancel_Click", xaml);
    }

    [Fact]
    public void EveryHandlerTheDialogNamesExists()
    {
        var handlers = Regex.Matches(DialogMarkup(), @"Click=""([A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        var code = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(),
            "McServerLauncher", "Views", "MissingDependenciesDialog.axaml.cs"));

        foreach (var handler in handlers)
            Assert.True(code.Contains($"void {handler}(", StringComparison.Ordinal),
                $"MissingDependenciesDialog.axaml enlaza «{handler}», que no existe en el code-behind");
    }

    [Fact]
    public void CancelIsWhatHappensIfTheWindowIsJustClosed()
    {
        var code = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(),
            "McServerLauncher", "Views", "MissingDependenciesDialog.axaml.cs"));

        // Closing with the X sets no choice, so the initial value is the answer. It has to be the
        // one that does not start a server the user may have thought better of.
        Assert.Contains("Choice { get; private set; } = MissingDependenciesChoice.Cancel;", code);
    }

    private static string DialogMarkup() => File.ReadAllText(Path.Combine(
        LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "MissingDependenciesDialog.axaml"));
}
