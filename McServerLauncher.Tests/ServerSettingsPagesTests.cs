using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Views.ServerSettings;

namespace McServerLauncher.Tests;

/// <summary>
/// Every settings page has its one row, and the row names things that exist.
/// </summary>
/// <remarks>
/// The facts about each page used to be three lists kept in step by hand. A field missing from one
/// of them was saved without lighting its page's dot, or lit it and was never saved — and nothing
/// failed. These do.
/// </remarks>
public class ServerSettingsPagesTests
{
    [Fact]
    public void EveryPageHasExactlyOneRow()
    {
        foreach (var page in Enum.GetValues<ServerSettingsPage>())
            Assert.Single(ServerSettingsPages.All, row => row.Page == page);
    }

    [Fact]
    public void EveryFieldARowNamesIsAConfigProperty()
    {
        foreach (var field in ServerSettingsPages.All.SelectMany(row => row.Fields))
            Assert.NotNull(typeof(ServerConfig).GetProperty(field));
    }

    [Fact]
    public void NoFieldOrKeyBelongsToTwoPages()
    {
        // Two pages with one field would both light up, and Discard on one would undo the other's.
        var fields = ServerSettingsPages.All.SelectMany(row => row.Fields).ToList();
        var keys = ServerSettingsPages.All.SelectMany(row => row.Keys).ToList();

        Assert.Equal(fields.Count, fields.Distinct().Count());
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryPageIsNamedInTheResourceFiles()
    {
        foreach (var row in ServerSettingsPages.All)
        {
            Assert.NotEqual(row.Title, Localizer.Get(row.Title));
            Assert.NotEqual(row.Hint, Localizer.Get(row.Hint));
            if (!string.IsNullOrEmpty(row.Group)) Assert.NotEqual(row.Group, Localizer.Get(row.Group));
        }
    }
}
