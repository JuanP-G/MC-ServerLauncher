using System.Security.Cryptography;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Gets an update's package onto disk and proves it is the one the publisher made, in the order
/// that makes a refusal cheap: everything that can say "no" is asked for before the download.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of updating that decides whether the package is installed, taken out of
/// <c>MainViewModel</c> so it can be read — and tested — on its own. What stays in the view model is
/// what is about the screen: the text in the banner, stopping the servers, the fallback to the
/// release page. What is here never touches either.
/// </para>
/// <para>
/// The order is the design. The checksum and the signature are fetched first, so a release that
/// lacks either is refused without costing the transfer of a ~35 MB installer. The checksum proves
/// the download is whole. It comes from the same release as the package, so it proves nothing about
/// who made it; the signature, checked against keys that do not live on GitHub, is what does, and it
/// is just as mandatory (see <see cref="UpdateSignature"/>). A package that fails either check is
/// deleted, so the next step cannot find it lying there.
/// </para>
/// <para>
/// None of the awaits here uses <c>ConfigureAwait(false)</c> on purpose: <c>onPhase</c>
/// is called back on the thread that started the update, which is the one allowed to change the
/// banner.
/// </para>
/// </remarks>
public sealed class AppUpdateCoordinator
{
    /// <summary>A package that passed both checks: where it is, and the SHA-256 it was verified as.</summary>
    /// <remarks>
    /// The hash travels with the path because it is what the installer is checked against once more,
    /// at the moment it runs, by <see cref="SelfUpdater"/>: the file sat on disk in between.
    /// </remarks>
    public sealed record VerifiedPackage(string Path, string Sha256);

    /// <summary>A step the user can be told about while it runs.</summary>
    public enum Phase
    {
        /// <summary>The package is being downloaded.</summary>
        Downloading,
        /// <summary>The download is being compared with the release's checksum.</summary>
        VerifyingChecksum,
        /// <summary>The download is being checked against the publisher's signature.</summary>
        VerifyingSignature,
    }

    private readonly IUpdateSource _source;
    private readonly IReadOnlyList<string>? _trustedKeys;

    /// <param name="source">Where the files come from. The GitHub release by default.</param>
    /// <param name="trustedKeys">The public keys whose signature is accepted; the built-in ones
    /// when null. For tests: the real ones have no private half in the repository.</param>
    public AppUpdateCoordinator(IUpdateSource? source = null, IReadOnlyList<string>? trustedKeys = null)
    {
        _source = source ?? new UpdateService();
        _trustedKeys = trustedKeys;
    }

    /// <summary>A new folder name for one update to download into.</summary>
    /// <remarks>
    /// Random on every run: a fixed name in the temporary folder could be planted, or replaced, by
    /// another local process between the download and the moment the package runs. The folder is not
    /// created here; <see cref="AtomicDownload"/> creates it.
    /// </remarks>
    public static string NewPackageFolder() =>
        Path.Combine(Path.GetTempPath(), SelfUpdater.PackageFolderPrefix + Path.GetRandomFileName());

    /// <summary>
    /// Downloads the package of <paramref name="offer"/> into <paramref name="folder"/> and returns
    /// it, with the checksum it matched, once the checksum and the signature both hold.
    /// </summary>
    /// <param name="offer">The release being installed.</param>
    /// <param name="folder">Where the package is saved; see <see cref="NewPackageFolder"/>.</param>
    /// <param name="onPhase">Told when each step starts. Called on the caller's context.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <exception cref="UserFacingException">
    /// With a message for the user: the release has no readable checksum or signature, the download
    /// does not match the checksum, or the signature is not the publisher's.
    /// </exception>
    public async Task<VerifiedPackage> DownloadAndVerifyAsync(UpdateService.UpdateInfo offer, string folder,
        Action<Phase>? onPhase = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(offer.PackageUrl))
            throw new ArgumentException("The release has no package for this platform.", nameof(offer));

        var dest = Path.Combine(folder, SelfUpdater.PackageFileName(offer.PackageName));

        // Both before the download, so a refusal does not cost the transfer.
        var expectedSha256 = await RequireChecksumAsync(offer, ct);
        var signature = await RequireSignatureAsync(offer, ct);

        onPhase?.Invoke(Phase.Downloading);
        await _source.DownloadInstallerAsync(offer.PackageUrl, dest, ct);

        onPhase?.Invoke(Phase.VerifyingChecksum);
        await DownloadVerifier.VerifyAsync(dest, expectedSha256, HashAlgorithmName.SHA256, ct);

        // The version and the name are the ones the release is offered under, so a package signed
        // as another version or another file does not pass as this one.
        onPhase?.Invoke(Phase.VerifyingSignature);
        var signed = await Task.Run(() => UpdateSignature.Verify(
            dest, offer.Version, offer.PackageName ?? "", signature, _trustedKeys), ct);
        if (!signed)
        {
            // A package that failed this is not left lying where the next step could find it.
            FileCleanup.TryDelete(dest);
            throw new UserFacingException(Localizer.Get("Msg_UpdateBadSignature"));
        }

        return new VerifiedPackage(dest, expectedSha256);
    }

    /// <summary>
    /// Removes a folder an update downloaded into. Never throws: whatever stays is cleared at a
    /// later start (see <see cref="SelfUpdater.DeleteOldPackages"/>).
    /// </summary>
    public static void DiscardFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch { /* cleared at a later start */ }
    }

    /// <summary>
    /// The package is about to become the app itself, so its checksum is REQUIRED, not best-effort.
    /// A missing or unreadable one means a broken (or tampered) release.
    /// </summary>
    private async Task<string> RequireChecksumAsync(UpdateService.UpdateInfo offer, CancellationToken ct)
    {
        var sha256 = string.IsNullOrEmpty(offer.ChecksumUrl) || string.IsNullOrEmpty(offer.PackageName)
            ? null
            : await _source.GetExpectedSha256Async(offer.ChecksumUrl, offer.PackageName, ct);

        return string.IsNullOrEmpty(sha256)
            ? throw new UserFacingException(Localizer.Get("Msg_UpdateNoChecksum"))
            : sha256;
    }

    /// <summary>
    /// Just as mandatory as the checksum: a release without a signature, or with one that does not
    /// read, is not installed.
    /// </summary>
    private async Task<string> RequireSignatureAsync(UpdateService.UpdateInfo offer, CancellationToken ct)
    {
        var signature = string.IsNullOrEmpty(offer.SignatureUrl)
            ? null
            : await _source.GetSignatureAsync(offer.SignatureUrl, ct);

        return string.IsNullOrEmpty(signature)
            ? throw new UserFacingException(Localizer.Get("Msg_UpdateNoSignature"))
            : signature;
    }
}
