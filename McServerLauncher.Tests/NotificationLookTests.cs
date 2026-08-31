using System.Reflection;
using System.Text.RegularExpressions;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Telling one notification from another without reading it.
/// </summary>
/// <remarks>
/// Every toast used to be the same panel with the same faint green border, whether somebody had
/// joined or the server had died. These check the two things that fixes: that every kind has a
/// level and a mark, and that a colour the user can type into a free-text box can never stop a
/// notification from being shown.
/// </remarks>
public class NotificationLookTests
{
    // --- The catalogue covers everything ---

    [Fact]
    public void EveryKindIsInTheCatalogue()
    {
        // The test that fires when somebody adds an eighth kind: without it, the new one silently
        // takes the neutral fallback and looks like "somebody left" no matter what it says.
        foreach (var kind in Enum.GetValues<NotificationKind>())
            Assert.Contains(NotificationCatalog.All, e => e.Kind == kind);
    }

    [Fact]
    public void EveryKindHasAMark()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
            Assert.False(string.IsNullOrWhiteSpace(NotificationCatalog.EmojiOf(kind)),
                $"{kind} no tiene emoji, así que solo se distingue por el color");
    }

    [Fact]
    public void TheMarksAreAllDifferent()
    {
        var marks = NotificationCatalog.All.Select(e => e.Emoji).ToList();

        // Two kinds sharing a mark is the same as neither having one: the whole point is that the
        // shape says which it is before the colour does.
        Assert.Equal(marks.Count, marks.Distinct().Count());
    }

    [Fact]
    public void EveryLevelIsActuallyUsed()
    {
        // A level nothing is shown at is a colour box the user can set and never see the effect of.
        foreach (var level in Enum.GetValues<NotificationLevel>())
            Assert.Contains(NotificationCatalog.All, e => e.Level == level);
    }

    [Fact]
    public void JoiningAndCrashingAreNotTheSameColour()
    {
        // Written out because it is the distinction that was asked for by name.
        Assert.NotEqual(NotificationCatalog.LevelOf(NotificationKind.PlayerJoined),
                        NotificationCatalog.LevelOf(NotificationKind.ServerCrashed));
    }

    // --- A typed colour cannot break a notification ---

    [Theory]
    [InlineData("#3FB950")]
    [InlineData("#fff")]
    [InlineData("#803FB950")]
    [InlineData("  #3FB950  ")]
    public void AColourThatCanBeDrawnIsAccepted(string hex) =>
        Assert.True(NotificationPalette.IsValid(hex));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("3FB950")]      // sin la almohadilla
    [InlineData("#E0")]         // a medio escribir, que es como está la casilla mientras se teclea
    [InlineData("#GGGGGG")]
    [InlineData("verde")]
    [InlineData("#3FB9501")]
    public void AnythingElseIsRefused(string? hex) => Assert.False(NotificationPalette.IsValid(hex));

    [Fact]
    public void ABadColourFallsBackToTheLevelDefault()
    {
        // The important half: this runs while building a toast that may be saying the server
        // crashed. Throwing there would lose the notification entirely.
        Assert.Equal(NotificationPalette.DefaultError,
            NotificationPalette.Sanitize("#nope", NotificationLevel.Error));

        Assert.Equal(NotificationPalette.DefaultSuccess,
            NotificationPalette.Sanitize(null, NotificationLevel.Success));
    }

    [Fact]
    public void EveryLevelHasItsOwnDefault()
    {
        var defaults = Enum.GetValues<NotificationLevel>()
            .Select(NotificationPalette.DefaultFor)
            .ToList();

        Assert.Equal(defaults.Count, defaults.Distinct().Count());
        Assert.All(defaults, d => Assert.True(NotificationPalette.IsValid(d)));
    }

    // --- The settings survive the round trip ---

    [Fact]
    public void CloningCarriesTheColours()
    {
        // The per-server override is seeded with a Clone, and the settings dialog edits one. A
        // colour missing here is a colour the user can change and watch revert with no explanation
        // — which is exactly what the comment on Clone() was written to prevent.
        var settings = new NotificationSettings
        {
            ColorInfo = "#111111",
            ColorSuccess = "#222222",
            ColorWarning = "#333333",
            ColorError = "#444444"
        };

        var copy = settings.Clone();

        Assert.Equal("#111111", copy.ColorInfo);
        Assert.Equal("#222222", copy.ColorSuccess);
        Assert.Equal("#333333", copy.ColorWarning);
        Assert.Equal("#444444", copy.ColorError);
    }

    [Fact]
    public void ColorForAndSetColorForAgreeOnEveryLevel()
    {
        var settings = new NotificationSettings();

        foreach (var level in Enum.GetValues<NotificationLevel>())
        {
            settings.SetColorFor(level, "#0A0B0C");
            Assert.Equal("#0A0B0C", settings.ColorFor(level));
        }
    }

    [Fact]
    public void ANewSettingsObjectStartsOnTheDefaults()
    {
        var settings = new NotificationSettings();

        foreach (var level in Enum.GetValues<NotificationLevel>())
            Assert.Equal(NotificationPalette.DefaultFor(level), settings.ColorFor(level));
    }

    // --- Which settings apply ---

    [Fact]
    public void AServerWithItsOwnSettingsIsColouredByThem()
    {
        var custom = new NotificationSettings { ColorError = "#ABCDEF" };
        var config = new ServerConfig { UseCustomNotifications = true, Notifications = custom };

        // Deciding "which settings apply" twice — once for whether to notify, once for the colour —
        // would drift, and the symptom would be a server notifying by its own rules and colouring
        // by somebody else's.
        Assert.Equal("#ABCDEF",
            NotificationPreferences.EffectiveFor(config).ColorFor(NotificationLevel.Error));
    }

    [Fact]
    public void AServerWithoutThemUsesTheGlobalOnes()
    {
        var previous = NotificationPreferences.Global;
        try
        {
            NotificationPreferences.Global = new NotificationSettings { ColorError = "#FEDCBA" };

            Assert.Equal("#FEDCBA", NotificationPreferences
                .EffectiveFor(new ServerConfig { UseCustomNotifications = false })
                .ColorFor(NotificationLevel.Error));

            // Null too: the update notification has no server behind it.
            Assert.Equal("#FEDCBA",
                NotificationPreferences.EffectiveFor(null).ColorFor(NotificationLevel.Error));
        }
        finally
        {
            NotificationPreferences.Global = previous;
        }
    }

    // --- The names the settings dialog builds at run time ---

    [Fact]
    public void EveryLevelHasALabelInEveryLanguage()
    {
        // "Notif_Level" + level is assembled at run time, so LocalizationTests cannot see these
        // four: it only scans string literals. This is the test that covers that gap.
        foreach (var level in Enum.GetValues<NotificationLevel>())
        {
            var key = "Notif_Level" + level;
            var text = Localizer.Get(key);

            Assert.NotEqual(key, text);   // Localizer.Get returns the key itself when it is missing
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    // --- The settings block binds to names that exist ---

    [Fact]
    public void EveryNameTheColourRowsBindToExistsOnTheDialog()
    {
        // SettingsDialog.axaml is x:CompileBindings="False": a mistyped name raises nothing and the
        // box simply never fills in.
        var xaml = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "SettingsDialog.axaml"));

        var names = Regex.Matches(xaml, @"\{Binding Notifications\.([A-Za-z0-9_]+)[,}]")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.True(
                typeof(NotificationSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"NotificationSettings no tiene ninguna propiedad «{name}», que SettingsDialog.axaml enlaza");
    }

    [Fact]
    public void TheFourColoursAreAllEditable()
    {
        var xaml = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Views", "SettingsDialog.axaml"));

        // One box per level, or a level the user cannot reach.
        foreach (var level in Enum.GetValues<NotificationLevel>())
            Assert.Contains($"{{Binding Notifications.Color{level}, Mode=TwoWay}}", xaml);
    }
}
