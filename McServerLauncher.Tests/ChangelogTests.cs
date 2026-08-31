using System.Reflection;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The "what's new" window: every release listed, in every language, and matching the build.
/// </summary>
/// <remarks>
/// This is the first thing a user sees after updating, and each release adds an entry to the table
/// plus five resx keys by hand. Miss one and the window shows the key name — "Whatsnew_1_10_2" —
/// which reads as a broken app at the exact moment the app is trying to explain itself.
/// </remarks>
public class ChangelogTests
{
    private static (Version Version, string Key)[] Entries() =>
        (( (Version, string)[] )typeof(Changelog)
            .GetField("Entries", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!)
        .Select(e => (e.Item1, e.Item2)).ToArray();

    [Fact]
    public void EveryReleaseHasItsNotesInEveryLanguage()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo(lang);

                foreach (var (version, key) in Entries())
                {
                    var notes = McServerLauncher.Localization.Localizer.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(notes) || notes == key,
                        $"faltan las notas de {version} en {lang} ({key})");
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
    }

    [Fact]
    public void TheNewestEntryIsTheVersionBeingBuilt()
    {
        // Bumping the version and forgetting the notes leaves the update silent about itself;
        // writing the notes and forgetting the bump ships them to nobody. Either way this fails.
        var built = typeof(Changelog).Assembly.GetName().Version!;
        var newest = Entries()[0].Version;

        // All four numbers: the fourth is the beta counter, and ignoring it would let 1.10.4.1 ship
        // with 1.10.4's notes and no complaint.
        Assert.Equal(
            new Version(built.Major, built.Minor, Math.Max(0, built.Build), Math.Max(0, built.Revision)),
            new Version(newest.Major, newest.Minor, Math.Max(0, newest.Build), Math.Max(0, newest.Revision)));
    }

    [Fact]
    public void TheTableIsOrderedNewestFirst()
    {
        // NotesSince walks the list in order and stops at the first entry on a fresh install, so a
        // row out of place shows the wrong release's notes rather than merely looking untidy.
        var versions = Entries().Select(e => e.Version).ToArray();

        Assert.Equal(versions.OrderByDescending(v => v).ToArray(), versions);
        Assert.Equal(versions.Distinct().Count(), versions.Length);
    }

    [Fact]
    public void AFreshInstallSeesOnlyTheNewest()
    {
        var newest = Entries()[0].Version;
        var section = Assert.Single(Changelog.NotesSince(lastSeen: null, current: newest));

        Assert.Contains(newest.Minor.ToString(), section.Version);
    }
}
