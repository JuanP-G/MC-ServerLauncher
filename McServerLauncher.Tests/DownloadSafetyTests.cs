using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What may come back from a server we download from: how much of it, from where, and whether it
/// has to be checkable.
/// </summary>
public class DownloadSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mcl-dl-" + Guid.NewGuid().ToString("N"));

    public DownloadSafetyTests() => Directory.CreateDirectory(_folder);

    private string In(string name) => Path.Combine(_folder, name);

    private static HttpContent Announcing(long length) => Content(length, true);

    private static HttpContent Silent(long length) => Content(length, false);

    private static HttpContent Content(long length, bool announce)
    {
        var content = new StreamContent(new MemoryStream(new byte[length]));
        if (announce) content.Headers.ContentLength = length;
        return content;
    }

    // ---- SEC-3: a ceiling on what is read ----

    [Fact]
    public async Task AFileLargerThanItsCeilingIsRefusedBeforeAByteIsWritten()
    {
        var dest = In("big.jar");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            AtomicDownload.ToFileAsync(Announcing(10_000), dest, maxBytes: 1_000));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task AFileThatGrowsPastItsCeilingWithoutAnnouncingItIsCutOffAndRemoved()
    {
        var dest = In("liar.jar");
        File.WriteAllText(dest, "the working copy");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            AtomicDownload.ToFileAsync(Silent(10_000), dest, maxBytes: 1_000));

        Assert.Equal("the working copy", File.ReadAllText(dest));   // what was there is untouched
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task AFileWithinItsCeilingIsKept()
    {
        var dest = In("ok.jar");

        await AtomicDownload.ToFileAsync(Announcing(900), dest, maxBytes: 1_000);

        Assert.Equal(900, new FileInfo(dest).Length);
    }

    [Fact]
    public async Task AnAnswerLargerThanItsCeilingIsNotReadToTheEnd()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppHttp.ReadCappedAsync(Silent(5_000), 1_000, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppHttp.ReadCappedAsync(Announcing(5_000), 1_000, CancellationToken.None));
    }

    [Fact]
    public async Task ATextAnswerWithinItsCeilingIsReadWhole()
    {
        var content = new StringContent("abc123  file.exe", Encoding.UTF8);

        Assert.Equal("abc123  file.exe",
            await AppHttp.ReadCappedStringAsync(content, AppHttp.MaxSmallTextBytes, CancellationToken.None));
    }

    // ---- SEC-4: where a link may point ----

    [Theory]
    [InlineData("https://github.com/JuanP-G/MC-ServerLauncher/releases/download/v1/x.exe", true)]
    [InlineData("https://GitHub.com/x", true)]
    [InlineData("http://github.com/x", false)]                        // not https
    [InlineData("https://github.com.evil.example/x", false)]          // a lookalike
    [InlineData("https://evilgithub.com/x", false)]                   // a suffix that is not a subdomain
    [InlineData("https://user:pw@github.com/x", false)]               // credentials in the address
    [InlineData("ftp://github.com/x", false)]
    [InlineData("//github.com/x", false)]                             // not absolute
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpsOnTheNamedHostIsAllowed(string? url, bool allowed)
    {
        Assert.Equal(allowed, AppHttp.IsAllowedUrl(url, "github.com"));
    }

    [Fact]
    public void ASubdomainOfAnAllowedHostIsAllowed() =>
        Assert.True(AppHttp.IsAllowedUrl("https://objects.githubusercontent.com/x", "githubusercontent.com"));

    [Fact]
    public void ARefusedAddressSaysSoInsteadOfBeingUsed() =>
        Assert.Throws<UserFacingException>(() => AppHttp.RequireAllowedUrl("https://evil.example/x.zip", "github.com"));

    [Fact]
    public void AReleaseAssetOnAnotherHostIsAsGoodAsAbsent()
    {
        using var doc = JsonDocument.Parse("""
            { "assets": [
                { "name": "good.exe", "browser_download_url": "https://github.com/JuanP-G/MC-ServerLauncher/releases/download/v1/good.exe" },
                { "name": "evil.exe", "browser_download_url": "https://evil.example/evil.exe" },
                { "name": "plain.exe", "browser_download_url": "http://github.com/plain.exe" }
            ] }
            """);

        var assets = UpdateService.ReadAssets(doc.RootElement);

        Assert.Equal(["good.exe"], assets.Keys);
    }

    // ---- SEC-5: a download with nothing to check it against ----

    [Fact]
    public async Task AFileWithNoExpectedHashIsRefusedAndRemoved()
    {
        var path = In("unchecked.jar");
        await File.WriteAllTextAsync(path, "payload");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            DownloadVerifier.VerifyAsync(path, null, HashAlgorithmName.SHA256));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AnEmptyExpectedHashIsRefusedToo()
    {
        var path = In("blank.jar");
        await File.WriteAllTextAsync(path, "payload");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            DownloadVerifier.VerifyAsync(path, "  ", HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task TheOptionalCheckLetsAFileWithNoHashThroughAndStillChecksOneWithAHash()
    {
        var path = In("optional.jar");
        await File.WriteAllTextAsync(path, "payload");

        await DownloadVerifier.VerifyIfProvidedAsync(path, null, HashAlgorithmName.SHA256);
        Assert.True(File.Exists(path));

        await Assert.ThrowsAsync<UserFacingException>(() =>
            DownloadVerifier.VerifyIfProvidedAsync(path, new string('0', 64), HashAlgorithmName.SHA256));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AMatchingHashPassesEitherWay()
    {
        var path = In("good.jar");
        await File.WriteAllTextAsync(path, "payload");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        await DownloadVerifier.VerifyAsync(path, hash, HashAlgorithmName.SHA256);
        await DownloadVerifier.VerifyIfProvidedAsync(path, hash.ToLowerInvariant(), HashAlgorithmName.SHA256);

        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { /* temp */ }
    }
}
