using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The <c>Exec=</c> line of a .desktop entry. Getting this wrong is uniquely unhelpful: the
/// shortcut is created, it looks right, and clicking it does nothing at all with no message
/// anywhere — so the only place the mistake can be caught is here.
/// </summary>
public class DesktopShortcutTests
{
    [Fact]
    public void OrdinaryPathIsJustQuoted()
    {
        // The overwhelmingly common case must stay untouched: no path contains any of the
        // characters below, so escaping must not invent anything.
        Assert.Equal("\"/home/u/Applications/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/Applications/x.AppImage"));
    }

    [Fact]
    public void SpacesSurviveInsideTheQuotes()
    {
        Assert.Equal("\"/home/u/My Apps/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/My Apps/x.AppImage"));
    }

    [Fact]
    public void PercentIsDoubledBecauseItIsAFieldCode()
    {
        // %f, %U and friends are expanded by the launcher after unquoting, so a literal % has to
        // be written twice. A folder called "Backup 100%" is all it takes to hit this.
        Assert.Equal("\"/home/u/100%%backup/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/100%backup/x.AppImage"));
    }

    [Fact]
    public void DollarIsEscapedTwice()
    {
        // Once for the Exec argument (\$) and once more because the whole value is a desktop-entry
        // string, where the backslash is itself the escape character.
        Assert.Equal("\"/home/u/\\\\$HOME/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/$HOME/x.AppImage"));
    }

    [Fact]
    public void BacktickIsEscapedTwice()
    {
        Assert.Equal("\"/home/u/\\\\`x/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/`x/x.AppImage"));
    }

    [Fact]
    public void BackslashEndsUpQuadrupled()
    {
        // One literal backslash: doubled by the Exec layer, doubled again by the string layer.
        // Unescaping in the other order gives back exactly one, which is the point.
        Assert.Equal("\"/home/u/a\\\\\\\\b/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/a\\b/x.AppImage"));
    }

    [Fact]
    public void QuoteIsEscaped()
    {
        Assert.Equal("\"/home/u/\\\\\"x/x.AppImage\"",
            DesktopShortcutService.Quote("/home/u/\"x/x.AppImage"));
    }

    [Fact]
    public void BackslashIsHandledBeforeTheOthers()
    {
        // The order trap: if $ were escaped before \, the backslash introduced by that escape would
        // then be escaped again and the result would be wrong while still looking plausible.
        // A path with both characters is what tells the two orders apart.
        var result = DesktopShortcutService.Quote("/a\\$b");

        // Correct: \ -> \\ -> \\\\ , and $ -> \$ -> \\$
        Assert.Equal("\"/a\\\\\\\\\\\\$b\"", result);
    }
}
