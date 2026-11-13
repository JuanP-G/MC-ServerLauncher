using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// A download goes only to the host that publishes it, and lands under a name Windows stores as written.
/// </summary>
/// <remarks>
/// The update and the JRE already refused a link on another host. A mod, a Paper or vanilla jar and
/// Geyser's artifacts followed whatever address the API response gave them, and a redirect could move
/// even the ones that had been checked. Every refusal here happens before a request leaves the test:
/// the checks run on the address, not on what comes back.
/// </remarks>
public class DownloadHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-hosts-" + Guid.NewGuid().ToString("N"));

    private string Dest(string name = "mod.jar")
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    // ---- which addresses count as a publisher's own ---------------------------------------------

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/x/versions/1/mod.jar", true)]
    [InlineData("https://modrinth.com/mod/x", true)]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/server.jar", false)]   // not Modrinth's
    [InlineData("http://cdn.modrinth.com/mod.jar", false)]                            // no TLS
    [InlineData("https://modrinth.com.evil.example/mod.jar", false)]                  // a lookalike prefix
    [InlineData("https://evilmodrinth.com/mod.jar", false)]                           // a lookalike suffix
    [InlineData("https://modrinth.com@evil.example/mod.jar", false)]                  // credentials hide the host
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("", false)]
    public void OnlyTheModrinthHostCountsAsModrinth(string url, bool allowed) =>
        Assert.Equal(allowed, AppHttp.IsAllowedUrl(url, TrustedHosts.Modrinth));

    [Theory]
    [InlineData("https://piston-meta.mojang.com/v1/packages/x/1.21.json", true)]
    [InlineData("https://piston-data.mojang.com/v1/objects/abc/server.jar", true)]
    [InlineData("https://launcher.mojang.com/v1/objects/abc/server.jar", true)]
    [InlineData("https://fill-data.papermc.io/v1/objects/abc/paper.jar", false)]
    public void MojangsHostsAreMojangsOnly(string url, bool allowed) =>
        Assert.Equal(allowed, AppHttp.IsAllowedUrl(url, TrustedHosts.Mojang));

    [Fact]
    public void EveryPublisherHasItsOwnList()
    {
        Assert.True(AppHttp.IsAllowedUrl("https://fill-data.papermc.io/x.jar", TrustedHosts.Paper));
        Assert.True(AppHttp.IsAllowedUrl("https://api.purpurmc.org/v2/purpur", TrustedHosts.Purpur));
        Assert.True(AppHttp.IsAllowedUrl("https://download.geysermc.org/v2/projects", TrustedHosts.GeyserMc));
        Assert.False(AppHttp.IsAllowedUrl("https://download.geysermc.org/x", TrustedHosts.Paper));
        Assert.False(AppHttp.IsAllowedUrl("https://cdn.modrinth.com/x.jar", TrustedHosts.Purpur));
    }

    // ---- redirects: the answer must come from an allowed host as well ---------------------------

    [Fact]
    public void AnAnswerFromAnotherHostAfterARedirectIsRefused()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/mod.jar"),
        };

        Assert.Throws<UserFacingException>(() =>
            AppHttp.RequireAllowedFinalUrl(resp, TrustedHosts.Modrinth));
    }

    [Fact]
    public void AnAnswerFromTheSameHostPasses()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.modrinth.com/mod.jar"),
        };

        AppHttp.RequireAllowedFinalUrl(resp, TrustedHosts.Modrinth);
    }

    [Fact]
    public void AnAnswerThatDroppedToHttpIsRefused()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.modrinth.com/mod.jar"),
        };

        Assert.Throws<UserFacingException>(() =>
            AppHttp.RequireAllowedFinalUrl(resp, TrustedHosts.Modrinth));
    }

    // ---- the downloads themselves ---------------------------------------------------------------

    [Fact]
    public async Task AModLinkOnAnotherHostIsNotFetched()
    {
        var dest = Dest();

        // A valid hash and a reachable-looking address: the host is the only thing wrong.
        await Assert.ThrowsAsync<UserFacingException>(() =>
            new ModrinthService().DownloadModAsync("https://evil.example/mod.jar", dest,
                new string('a', 128), null));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task AServerJarOnAnotherHostIsNotFetched()
    {
        var dest = Dest("server.jar");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            VerifiedJarDownload.ToFileAsync(new HttpClient(), "https://evil.example/server.jar", dest,
                new string('a', 64), HashAlgorithmName.SHA256, null,
                TrustedHosts.Paper, requireChecksum: true));

        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task PaperAndMojangJarsWithoutAChecksumAreNotInstalled()
    {
        var dest = Dest("server.jar");

        // The address is fine; it is the missing hash that stops it, before any request.
        var refused = await Assert.ThrowsAsync<UserFacingException>(() =>
            VerifiedJarDownload.ToFileAsync(new HttpClient(), "https://fill-data.papermc.io/x.jar", dest,
                null, HashAlgorithmName.SHA256, null, TrustedHosts.Paper, requireChecksum: true));

        Assert.Contains("server.jar", refused.Message);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task TheVersionManifestMustPointAtMojang()
    {
        var versions = new MinecraftVersionService();
        var hostile = new MinecraftVersion { Id = "9.9", Type = "release", Url = "https://evil.example/9.9.json" };

        await Assert.ThrowsAsync<UserFacingException>(() => versions.GetVersionDetailsAsync(hostile));
    }

    [Fact]
    public async Task AnImageOnAnotherHostIsNeverRequested()
    {
        // Returns null straight away: nothing is fetched, nothing is cached, the placeholder stays.
        Assert.Null(await ImageCache.GetAsync("https://evil.example/pixel.png"));
        Assert.Null(await ImageCache.GetAsync("http://cdn.modrinth.com/icon.png"));
        Assert.Null(await ImageCache.GetAsync("https://modrinth.com@evil.example/icon.png"));
    }

    // ---- file names ----------------------------------------------------------------------------

    [Theory]
    [InlineData("NUL.jar")]
    [InlineData("nul.jar")]
    [InlineData("Con.tar.jar")]            // the device is what precedes the first dot
    [InlineData("nul .jar")]               // and the spaces before the dot
    [InlineData("COM1.jar")]
    [InlineData("LPT9.jar")]
    [InlineData("COM\u00B9.jar")]
    [InlineData("mod.jar:stream")]         // an NTFS alternate data stream
    [InlineData("mod.jar.")]               // Windows would store it without the dot
    [InlineData("mod.jar ")]
    [InlineData(" mod.jar")]
    [InlineData("mod?.jar")]
    [InlineData("mod*.jar")]
    [InlineData("mo|d.jar")]
    [InlineData("mod\u0007.jar")]
    public void AFileNameWindowsWouldNotStoreAsWrittenIsRefused(string name) =>
        Assert.Throws<UserFacingException>(() => AtomicDownload.PathIn(Dest(), name, ".jar"));

    [Fact]
    public void ANameLongerThanAnyRealModIsRefused()
    {
        var name = new string('a', 201) + ".jar";

        Assert.Throws<UserFacingException>(() => AtomicDownload.PathIn(Dest(), name, ".jar"));
    }

    [Theory]
    [InlineData("startup.bat")]
    [InlineData("payload.dll")]
    [InlineData("mod.jar.exe")]
    [InlineData(".jar")]                   // an extension with no name before it
    [InlineData("jar")]
    public void TheModsFolderOnlyTakesJars(string name) =>
        Assert.Throws<UserFacingException>(() => AtomicDownload.PathIn(Dest(), name, ".jar"));

    [Theory]
    [InlineData("sodium-fabric-0.6.0+mc1.21.jar")]
    [InlineData("Explorify v1.6.5.mod.jar")]
    [InlineData("Mod.JAR")]
    [InlineData("console.jar")]            // starts like a device name but is not one
    [InlineData("nullable.jar")]
    public void OrdinaryJarNamesAreKept(string name) =>
        Assert.Equal(Path.Combine(_dir, name), AtomicDownload.PathIn(_dir, name, ".jar"));

    // ---- links opened in the browser ------------------------------------------------------------

    [Theory]
    [InlineData("https://modrinth.com/mod/sodium", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("https://modrinth.com@evil.example/", false)]
    [InlineData("https://user:pass@example.com/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("calc.exe", false)]
    public void OnlyPlainWebLinksOpenInTheBrowser(string url, bool opens) =>
        Assert.Equal(opens, BrowserLauncher.IsWebUrl(url));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
