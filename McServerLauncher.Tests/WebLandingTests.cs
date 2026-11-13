using System.Text.Json;

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

    // ---------------------------------------------------------------- audit 2026-11-09 (#162)

    private static string WebRoot() => Path.Combine(LocalizationTests.RepoRoot(), "web");

    /// <summary>Every page the site serves: the landings, the guides and the 404.</summary>
    private static IEnumerable<string> AllPages() =>
        Directory.EnumerateFiles(WebRoot(), "*.html", SearchOption.AllDirectories)
            .Where(p =>
            {
                var rel = Path.GetRelativePath(WebRoot(), p).Replace('\\', '/');
                return !rel.StartsWith("_i18n/") && !rel.StartsWith("_tools/")
                    // The search console's ownership file is a .html with one line and no page in it.
                    && File.ReadAllText(p).Contains("<html");
            });

    private static JsonElement[] VercelRules(string source)
    {
        using var doc = JsonDocument.Parse(Web("vercel.json"));
        return doc.RootElement.GetProperty("headers").EnumerateArray()
            .Where(r => r.GetProperty("source").GetString() == source)
            .Select(r => r.Clone()).ToArray();
    }

    private static Dictionary<string, string> HeadersOf(JsonElement rule) =>
        rule.GetProperty("headers").EnumerateArray().ToDictionary(
            h => h.GetProperty("key").GetString()!, h => h.GetProperty("value").GetString()!);

    /// <summary>
    /// The site used to be served with no security header at all. Vercel reads these from
    /// <c>web/vercel.json</c>; the content policy itself travels in a <c>&lt;meta&gt;</c> so GitHub
    /// Pages gets it too, and the header adds the part a meta cannot (frame-ancestors).
    /// </summary>
    [Fact]
    public void EveryResponseCarriesTheSecurityHeaders()
    {
        var rule = Assert.Single(VercelRules("/(.*)"));
        var headers = HeadersOf(rule);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("frame-ancestors 'none'", headers["Content-Security-Policy"]);
        Assert.Contains("camera=()", headers["Permissions-Policy"]);
        Assert.DoesNotContain("Cache-Control", headers.Keys);
    }

    /// <summary>
    /// Only the files whose address carries their fingerprint (<c>?v=</c>) may be cached for a year:
    /// the images and the news feed keep their address when they change.
    /// </summary>
    [Fact]
    public void OnlyFingerprintedAssetsAreCachedForever()
    {
        var rule = Assert.Single(VercelRules("/assets/(.*)"));
        var headers = HeadersOf(rule);

        Assert.Equal("public, max-age=31536000, immutable", headers["Cache-Control"]);
        var condition = Assert.Single(rule.GetProperty("has").EnumerateArray());
        Assert.Equal("query", condition.GetProperty("type").GetString());
        Assert.Equal("v", condition.GetProperty("key").GetString());

        using var doc = JsonDocument.Parse(Web("vercel.json"));
        var immutable = doc.RootElement.GetProperty("headers").EnumerateArray()
            .Count(r => HeadersOf(r).GetValueOrDefault("Cache-Control", "").Contains("immutable"));
        Assert.Equal(1, immutable);
    }

    /// <summary>
    /// A script from another site runs with the whole page in reach and, without an integrity
    /// attribute, changes whenever its owner decides. The counter is a copy of GoatCounter's own
    /// file, kept in <c>assets/</c>, and the policy lets scripts come from the site only.
    /// </summary>
    [Fact]
    public void NoPageLoadsAScriptFromAnotherSite()
    {
        foreach (var page in AllPages())
        {
            var html = File.ReadAllText(page);
            var name = Path.GetRelativePath(WebRoot(), page);

            // The index explains in a comment where count.js came from, so the address may appear
            // as text; what must never appear is a script tag that points at another host.
            Assert.DoesNotMatch(@"<script[^>]*\ssrc=""(https?:)?//", html);
            Assert.DoesNotMatch(@"script-src[^;]*(gc\.zgo\.at|https?:)", html);
            Assert.Contains("script-src 'self';", html);
            Assert.True(!html.Contains("script-src 'self' "), name + ": script-src must not name another host");
        }

        Assert.Contains("ISC", Web("assets", "count.js"));
        Assert.Matches(@"src=""assets/count\.js\?v=[0-9a-f]{10}""", Web("index.html"));
    }

    /// <summary>
    /// One manifest per language: opening the installed shortcut from an English page used to land
    /// on the Spanish one, and the description was always Spanish.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("pt")]
    public void EveryLanguageHasItsOwnManifest(string code)
    {
        using var doc = JsonDocument.Parse(Web("assets", $"site.{code}.webmanifest"));
        using var spanish = JsonDocument.Parse(Web("assets", "site.webmanifest"));

        Assert.Equal($"../{code}/", doc.RootElement.GetProperty("start_url").GetString());
        Assert.NotEqual(spanish.RootElement.GetProperty("description").GetString(),
            doc.RootElement.GetProperty("description").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("lang").GetString()));

        Assert.Contains($"assets/site.{code}.webmanifest\"", Web(code, "index.html"));
        foreach (var guide in Directory.EnumerateFiles(Path.Combine(WebRoot(), code, "guides"), "index.html",
                     SearchOption.AllDirectories))
            Assert.Contains($"assets/site.{code}.webmanifest\"", File.ReadAllText(guide));
    }

    /// <summary>The English page said en-GB to the version list and en_US to everyone else.</summary>
    [Fact]
    public void TheEnglishPageUsesOneLocale()
    {
        var html = Web("en", "index.html");

        Assert.Contains("data-locale=\"en-US\"", html);
        Assert.Contains("content=\"en_US\"", html);
        Assert.DoesNotContain("en-GB", html);
    }

    /// <summary>The French page tutoys everywhere; two sentences said "vous".</summary>
    [Fact]
    public void TheFrenchLandingKeepsOneRegister()
    {
        Assert.DoesNotMatch(@"(?i)\b(vous|votre|vos)\b", Web("fr", "index.html"));
    }

    /// <summary>
    /// The headline arrives as plain text and the script, which is deferred, turns it into words
    /// that drop in one by one: for an instant the whole sentence showed, vanished and was rebuilt.
    /// The style hides it until the script has split it, and shows everything again if the script
    /// never runs or the visitor asked for less motion.
    /// </summary>
    [Fact]
    public void TheHeadlineIsHiddenUntilItIsSplitAndNeverStaysHidden()
    {
        var css = Web("assets", "site.css");
        var js = Web("assets", "site.js");

        Assert.Contains("@media (scripting: enabled){\n  .build:not(.split){opacity:0}", css.Replace("\r\n", "\n"));
        Assert.Contains("html:not(.reveal-ready) .build:not(.split)", css);
        Assert.Contains("el.classList.add('split')", js);
        Assert.Contains("classList.add('reveal-ready')", js);
        Assert.Matches(@"prefers-reduced-motion:reduce\)\{\s*\.reveal,\.build,", css);
    }

    /// <summary>
    /// GitHub Pages ignores <c>vercel.json</c>; it would only be one more file for crawlers to find.
    /// </summary>
    [Fact]
    public void ThePagesCopyDoesNotShipTheVercelFile()
    {
        var workflow = File.ReadAllText(Path.Combine(LocalizationTests.RepoRoot(), ".github", "workflows", "docs.yml"));

        Assert.Contains("_pages\\vercel.json", workflow);
    }

    /// <summary>
    /// An image nobody links is weight in the repository and a file for a crawler to fetch. Every
    /// image at the top of <c>assets/</c> must be named by a page, a style, a script or a generator.
    /// </summary>
    [Fact]
    public void NoImageAtTheTopOfAssetsIsUnused()
    {
        var sources = Directory.EnumerateFiles(WebRoot(), "*", SearchOption.AllDirectories)
            .Where(p => new[] { ".html", ".css", ".js", ".py", ".json", ".xml", ".txt" }
                .Contains(Path.GetExtension(p)))
            .Select(File.ReadAllText).ToArray();
        var images = Directory.EnumerateFiles(Path.Combine(WebRoot(), "assets"))
            .Where(p => new[] { ".png", ".jpg", ".webp", ".svg" }.Contains(Path.GetExtension(p)));

        foreach (var image in images)
        {
            var name = Path.GetFileName(image);
            Assert.True(sources.Any(s => s.Contains(name)), name + " is not referenced anywhere in web/");
        }
    }
}
