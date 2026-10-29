using System.Security.Cryptography;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The order in which an update is checked, and what each check refuses, run against files made on
/// the spot instead of a release.
/// </summary>
/// <remarks>
/// <para>
/// This is the flow that ends in an installer running with elevation, and until it was taken out of
/// the view model it could only be tried by installing a release. What is pinned here: a release
/// without a checksum or a signature is refused <em>before</em> anything is downloaded, a package that
/// does not match its checksum or is not signed by the publisher is refused and does not stay on
/// disk, and the steps are announced in the order they run.
/// </para>
/// <para>
/// The key is generated per test, so no private key lives in the repository.
/// </para>
/// </remarks>
public class AppUpdateCoordinatorTests : IDisposable
{
    private const string Version = "1.99.0";
    private const string PackageName = "MC-ServerLauncher-Setup-1.99.0.exe";
    private const string Content = "pretend this is the installer";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcsl-coordinator-" + Path.GetRandomFileName());
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public AppUpdateCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    // --- a stand-in for the GitHub release ---

    private sealed class FakeSource : IUpdateSource
    {
        public string? Checksum;
        public string? Signature;
        public string Package = Content;
        public int Downloads;

        public Task<string?> GetExpectedSha256Async(string sha256SumsUrl, string fileName, CancellationToken ct = default) =>
            Task.FromResult(Checksum);

        public Task<string?> GetSignatureAsync(string signatureUrl, CancellationToken ct = default) =>
            Task.FromResult(Signature);

        public async Task<string> DownloadInstallerAsync(string url, string destPath, CancellationToken ct = default)
        {
            Downloads++;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await File.WriteAllTextAsync(destPath, Package, ct);
            return destPath;
        }
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private string Sign(string content, string version = Version, string fileName = PackageName) =>
        Convert.ToBase64String(_key.SignData(
            UpdateSignature.SignedMessage(version, fileName, Sha256(content)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    private FakeSource GoodSource() => new() { Checksum = Sha256(Content), Signature = Sign(Content) };

    private AppUpdateCoordinator Coordinator(FakeSource source) =>
        new(source, [Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())]);

    private static UpdateService.UpdateInfo Offer(string? checksumUrl = "https://example.test/SHA256SUMS.txt",
        string? signatureUrl = "https://example.test/package.sig2", string? packageUrl = "https://example.test/package") =>
        new(Version, "https://example.test/release", packageUrl, PackageName, checksumUrl,
            SignatureUrl: signatureUrl);

    private string Folder() => Path.Combine(_dir, "update");

    // --- the way through ---

    [Fact]
    public async Task APackageThatMatchesAndIsSignedComesBackAsAPath()
    {
        var path = (await Coordinator(GoodSource()).DownloadAndVerifyAsync(Offer(), Folder())).Path;

        Assert.Equal(Path.Combine(Folder(), PackageName), path);
        Assert.Equal(Content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TheStepsAreAnnouncedInTheOrderTheyRun()
    {
        var seen = new List<AppUpdateCoordinator.Phase>();

        await Coordinator(GoodSource()).DownloadAndVerifyAsync(Offer(), Folder(), seen.Add);

        Assert.Equal(
        [
            AppUpdateCoordinator.Phase.Downloading,
            AppUpdateCoordinator.Phase.VerifyingChecksum,
            AppUpdateCoordinator.Phase.VerifyingSignature,
        ], seen);
    }

    // --- refused before the download ---

    [Fact]
    public async Task AReleaseWithoutAChecksumIsRefusedWithoutDownloadingAnything()
    {
        var source = GoodSource();
        source.Checksum = null;

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(), Folder()));

        Assert.Equal(Localizer.Get("Msg_UpdateNoChecksum"), ex.Message);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task AReleaseThatDoesNotNameItsChecksumFileIsRefusedToo()
    {
        var source = GoodSource();

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(checksumUrl: null), Folder()));

        Assert.Equal(Localizer.Get("Msg_UpdateNoChecksum"), ex.Message);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task AReleaseWithoutASignatureIsRefusedWithoutDownloadingAnything()
    {
        var source = GoodSource();
        source.Signature = null;

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(), Folder()));

        Assert.Equal(Localizer.Get("Msg_UpdateNoSignature"), ex.Message);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task AReleaseThatDoesNotNameItsSignatureIsRefusedToo()
    {
        var source = GoodSource();

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(signatureUrl: null), Folder()));

        Assert.Equal(Localizer.Get("Msg_UpdateNoSignature"), ex.Message);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task AReleaseWithNoPackageForThisPlatformIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Coordinator(GoodSource()).DownloadAndVerifyAsync(Offer(packageUrl: null), Folder()));
    }

    // --- refused after it ---

    [Fact]
    public async Task APackageThatDoesNotMatchItsChecksumIsRefusedAndDoesNotStayOnDisk()
    {
        var source = GoodSource();
        source.Package = "something else was served";

        await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(), Folder()));

        Assert.False(File.Exists(Path.Combine(Folder(), PackageName)));
    }

    [Fact]
    public async Task APackageSignedByAnotherKeyIsRefusedAndDeleted()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var source = GoodSource();
        source.Signature = Convert.ToBase64String(stranger.SignData(
            UpdateSignature.SignedMessage(Version, PackageName, Sha256(Content)),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(), Folder()));

        Assert.Equal(Localizer.Get("Msg_UpdateBadSignature"), ex.Message);
        Assert.False(File.Exists(Path.Combine(Folder(), PackageName)));
    }

    [Fact]
    public async Task TheSignatureOfAnotherVersionDoesNotPassForThisOne()
    {
        var source = GoodSource();
        source.Signature = Sign(Content, version: "1.21.1");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(), Folder()));

        Assert.False(File.Exists(Path.Combine(Folder(), PackageName)));
    }

    [Fact]
    public async Task TheSignatureOfAnotherFileDoesNotPassForThisOne()
    {
        var source = GoodSource();
        source.Signature = Sign(Content, fileName: "MC-ServerLauncher.AppImage");

        await Assert.ThrowsAsync<UserFacingException>(() =>
            Coordinator(source).DownloadAndVerifyAsync(Offer(), Folder()));
    }

    // --- the folder ---

    [Fact]
    public void EveryUpdateGetsANewFolderUnderTheTemporaryOne()
    {
        var first = AppUpdateCoordinator.NewPackageFolder();
        var second = AppUpdateCoordinator.NewPackageFolder();

        Assert.NotEqual(first, second);
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(first)!.TrimEnd(Path.DirectorySeparatorChar));
        Assert.StartsWith(SelfUpdater.PackageFolderPrefix, Path.GetFileName(first));
    }

    [Fact]
    public void DiscardingAFolderRemovesItAndNeverThrows()
    {
        var folder = Path.Combine(_dir, "to-discard");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "installer.exe"), "x");

        AppUpdateCoordinator.DiscardFolder(folder);
        AppUpdateCoordinator.DiscardFolder(folder);   // already gone: still nothing to report

        Assert.False(Directory.Exists(folder));
    }
}
