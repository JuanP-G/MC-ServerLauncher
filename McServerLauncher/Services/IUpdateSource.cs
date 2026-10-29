namespace McServerLauncher.Services;

/// <summary>
/// Where an update's files come from: the checksum list, the signature and the package itself.
/// </summary>
/// <remarks>
/// <see cref="UpdateService"/> is the real one, and reads them from the GitHub release. The seam
/// exists so <see cref="AppUpdateCoordinator"/> can be run in a test against files made on the spot:
/// the order of the checks, and what each one refuses, is what the update has to get right, and it
/// cannot be tried against a real release without installing it.
/// </remarks>
public interface IUpdateSource
{
    /// <summary>The expected SHA-256 of <paramref name="fileName"/>, or null when it cannot be read.</summary>
    Task<string?> GetExpectedSha256Async(string sha256SumsUrl, string fileName, CancellationToken ct = default);

    /// <summary>The publisher's signature of the package, or null when it cannot be read.</summary>
    Task<string?> GetSignatureAsync(string signatureUrl, CancellationToken ct = default);

    /// <summary>Downloads the package to <paramref name="destPath"/> and returns that path.</summary>
    Task<string> DownloadInstallerAsync(string url, string destPath, CancellationToken ct = default);
}
