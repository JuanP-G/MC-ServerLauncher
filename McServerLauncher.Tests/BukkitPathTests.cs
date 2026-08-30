using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The two characters Paper refuses to run from, and the silence they used to fail in.
/// </summary>
/// <remarks>
/// A NeoForge server living in a folder called <c>Java+Bedrock</c> worked for weeks. Converted to
/// Paper it exited instantly on every start, three times, and the one line explaining why was in
/// English and buried under two more attempts. Both facts below were checked against a real Paper
/// 26.2 jar rather than taken from documentation.
/// </remarks>
public class BukkitPathTests
{
    [Theory]
    [InlineData(@"C:\Users\JPG\Downloads\Java+Bedrock", '+')]
    [InlineData(@"C:\servers\wow!\survival", '!')]
    [InlineData(@"C:\Users\JPG\Downloads\Java-Bedrock", null)]
    [InlineData(@"C:\servers\survival", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheOffendingCharacterIsFound(string? path, char? expected) =>
        Assert.Equal(expected, BukkitPathRule.OffendingCharacter(path));

    [Fact]
    public void AParentFolderCountsToo()
    {
        // The check is on the working directory, so a "+" anywhere above the server rejects it just
        // the same — and that one is far harder to spot, because the server's own name looks fine.
        Assert.Equal('+', BukkitPathRule.OffendingCharacter(@"C:\my+stuff\servers\survival"));
    }

    [Fact]
    public void OnlyThePluginFamilyCares()
    {
        const string path = @"C:\Users\JPG\Downloads\Java+Bedrock";

        Assert.True(BukkitPathRule.Rejects(path, ServerType.Paper));
        Assert.True(BukkitPathRule.Rejects(path, ServerType.Purpur));

        // This is the whole shape of the bug: the folder worked for months under a mod loader and
        // stopped working the moment it became Paper, with nothing linking the two.
        Assert.False(BukkitPathRule.Rejects(path, ServerType.NeoForge));
        Assert.False(BukkitPathRule.Rejects(path, ServerType.Fabric));
        Assert.False(BukkitPathRule.Rejects(path, ServerType.Forge));
        Assert.False(BukkitPathRule.Rejects(path, ServerType.Vanilla));
    }

    [Fact]
    public void BothRefusalsAreRecognised()
    {
        // Two different programs, two different wordings, both observed from a real Paper jar:
        // Paperclip rejects "!" before the server exists, CraftBukkit rejects "+" once it does.
        Assert.True(BukkitPathRule.IsPathRejection(
            "Cannot run server in a directory with ! or + in the pathname. Please rename the affected folders and try again."));
        Assert.True(BukkitPathRule.IsPathRejection(
            "Paperclip may not run in a directory containing '!'. Please rename the affected folder."));
    }

    [Fact]
    public void OrdinaryLinesAreNot()
    {
        Assert.False(BukkitPathRule.IsPathRejection("[14:14:55 INFO]: Done (17.000s)! For help, type \"help\""));
        Assert.False(BukkitPathRule.IsPathRejection("Starting org.bukkit.craftbukkit.Main"));
        Assert.False(BukkitPathRule.IsPathRejection(""));
    }

    [Theory]
    [InlineData(@"C:\Users\JPG\Downloads\Java+Bedrock (paper)", true)]
    [InlineData(@"C:\servers\wow!", true)]
    // The character is above the server: renaming that folder would move everything else under it
    // too, which is not the app's to decide.
    [InlineData(@"C:\my+stuff\servers\survival", false)]
    [InlineData(@"C:\servers\survival", false)]
    public void RenamingIsOnlyOfferedForTheServersOwnFolder(string path, bool expected) =>
        Assert.Equal(expected, BukkitPathRule.IsInServerFolderName(path));

    [Fact]
    public void TheSuggestedNameReplacesTheCharacterRatherThanDroppingIt()
    {
        // "JavaBedrock" reads like a typo; "Java-Bedrock" reads like the name that was meant.
        var suggested = BukkitPathRule.SuggestCleanPath(@"C:\Users\JPG\Downloads\Java+Bedrock (paper)");

        Assert.Equal("Java-Bedrock (paper)", Path.GetFileName(suggested));
        Assert.Equal(@"C:\Users\JPG\Downloads", Path.GetDirectoryName(suggested));
    }

    [Fact]
    public void NothingIsSuggestedWhenThereIsNothingToFixOrItIsNotOurs()
    {
        Assert.Null(BukkitPathRule.SuggestCleanPath(@"C:\servers\survival"));
        Assert.Null(BukkitPathRule.SuggestCleanPath(@"C:\my+stuff\servers\survival"));
        Assert.Null(BukkitPathRule.SuggestCleanPath(null));
    }

    [Fact]
    public void TheRenameStringsExistInEveryLanguage()
    {
        string[] keys =
        {
            "Msg_BukkitPathRenameTitle", "Msg_BukkitPathRenameConfirm",
            "Msg_BukkitPathRenamedFmt", "Msg_BukkitPathRenameExists", "Msg_NotRetryingFinal",
        };

        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo(lang);

                foreach (var key in keys)
                {
                    var value = McServerLauncher.Localization.Localizer.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(value) || value == key, $"falta {key} en {lang}");
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
    }

    [Fact]
    public void TheExplanationExistsInEveryLanguage()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            foreach (var lang in new[] { "es", "en", "pt", "fr", "de" })
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    System.Globalization.CultureInfo.GetCultureInfo(lang);

                foreach (var key in new[] { "Msg_BukkitPathFmt", "Msg_BukkitPathUnknown" })
                {
                    var value = McServerLauncher.Localization.Localizer.Get(key);
                    Assert.False(string.IsNullOrWhiteSpace(value) || value == key, $"falta {key} en {lang}");
                }

                // The explanation is written to the same console it is read from; if it matched
                // itself the warning would feed on its own output.
                Assert.False(BukkitPathRule.IsPathRejection(
                    string.Format(McServerLauncher.Localization.Localizer.Get("Msg_BukkitPathFmt"), '+')));
            }
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = original; }
    }
}
