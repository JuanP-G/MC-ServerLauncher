namespace McServerLauncher.Tests;

/// <summary>
/// What the landing page promises a visitor, checked in the files that are served.
/// </summary>
/// <remarks>
/// <para>
/// The generated pages are checked against their sources by the <c>web-pages</c> job in
/// <c>tests.yml</c>; these tests check the sources say the right thing. Both are reading, not
/// running a browser: the script is exercised by hand in the browser pane, and what these catch is
/// the regression that is easy to make and impossible to see in a review, such as one language
/// losing a button.
/// </para>
/// </remarks>
public class WebLandingTests
{
    private static string Web(params string[] parts) =>
        File.ReadAllText(Path.Combine([LocalizationTests.RepoRoot(), "web", .. parts]));

    public static TheoryData<string> Landings => new() { "index.html", "en/index.html", "de/index.html", "fr/index.html", "pt/index.html" };

    /// <summary>
    /// The Apple Silicon build does not start on an Intel Mac, and both are in every release. One
    /// "Download .dmg" button used to pick whichever was first in the list.
    /// </summary>
    [Theory]
    [MemberData(nameof(Landings))]
    public void EveryLanguageOffersBothMacBuilds(string page)
    {
        var html = Web(page.Split('/'));

        Assert.Contains("data-dl=\"mac\"", html);
        Assert.Contains("data-dl=\"mac-intel\"", html);
    }

    [Fact]
    public void TheIntelBuildIsNotWhatTheAppleSiliconButtonPicks()
    {
        var js = Web("assets", "site.js");

        Assert.Contains("mac:         function(n){ return /\\.dmg$/i.test(n) && !INTEL.test(n); }", js);
        Assert.Contains("'mac-intel': function(n){ return /\\.dmg$/i.test(n) && INTEL.test(n); }", js);
    }

    /// <summary>
    /// The address of each download comes from GitHub's answer. The release list checks its links
    /// are GitHub's; the buttons that download the file must too.
    /// </summary>
    [Fact]
    public void DownloadButtonsOnlyFollowAddressesOfThisRepositorysReleases()
    {
        var js = Web("assets", "site.js");

        Assert.Contains("var DOWNLOAD_PREFIX = 'https://github.com/' + REPO + '/releases/download/';", js);
        Assert.Contains("asset.browser_download_url.indexOf(DOWNLOAD_PREFIX) === 0", js);
    }

    /// <summary>
    /// With the language code and the name as two separate pieces of text a screen reader said
    /// "ESEspañol", and read every name in the voice of the page's own language.
    /// </summary>
    [Theory]
    [MemberData(nameof(Landings))]
    public void LanguageNamesCarryTheirOwnLanguage(string page)
    {
        var html = Web(page.Split('/'));

        Assert.DoesNotContain("<b>ES</b>", html);
        Assert.Contains("<span lang=\"es\">Español</span>", html);
        Assert.Contains("<span lang=\"en\">English</span>", html);
        Assert.Contains("<span lang=\"de\">Deutsch</span>", html);
    }
}
