using System.IO;
using System.Security.Cryptography;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Verifies a downloaded file's hash against the checksum an official API (Mojang, Adoptium, Paper)
/// already returns alongside the download URL. On mismatch the file is deleted and an exception is
/// thrown, so a corrupted or tampered download is never silently trusted.
/// </summary>
/// <remarks>
/// Whether a hash <em>must</em> exist is a decision, and it is made at the call: <see cref="VerifyAsync"/>
/// refuses a download it has no hash for, and <see cref="VerifyIfProvidedAsync"/> is the one that lets
/// it through. A single method that quietly did nothing for an empty hash made forgetting to demand
/// one look the same as having chosen not to.
/// <para>
/// A hash from the same API that gave the link catches a truncated or corrupted file; it cannot
/// catch an API that is itself lying, and an MD5 (Purpur publishes nothing else) not even a crafted
/// file. Where the file is about to be run, the caller requires one and the publisher's host.
/// </para>
/// </remarks>
public static class DownloadVerifier
{
    /// <summary>
    /// Verifies <paramref name="filePath"/> against <paramref name="expectedHex"/> using
    /// <paramref name="algorithm"/>. Without an expected hash the file is deleted and this throws:
    /// nothing is accepted that could not be checked.
    /// </summary>
    public static async Task VerifyAsync(string filePath, string? expectedHex, HashAlgorithmName algorithm,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            FileCleanup.TryDelete(filePath);
            throw new UserFacingException(
                Localizer.Format("Msg_ChecksumRequiredFmt", AtomicDownload.DisplayName(filePath)));
        }

        var actualHex = await ComputeHashAsync(filePath, algorithm, ct);
        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            FileCleanup.TryDelete(filePath);
            throw new UserFacingException(
                Localizer.Format("Msg_ChecksumMismatchFmt",
                    AtomicDownload.DisplayName(filePath)));
        }
    }

    /// <summary>
    /// Like <see cref="VerifyAsync"/>, but a null or empty expected hash means the API published none
    /// for this asset and the file is accepted unchecked. For downloads where that is the
    /// documented rule; saying so here keeps it visible at the call.
    /// </summary>
    public static Task VerifyIfProvidedAsync(string filePath, string? expectedHex, HashAlgorithmName algorithm,
        CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(expectedHex)
            ? Task.CompletedTask
            : VerifyAsync(filePath, expectedHex, algorithm, ct);

    /// <summary>
    /// Computes the hex-encoded hash of a file in a streaming fashion (no full read into memory).
    /// </summary>
    public static async Task<string> ComputeHashAsync(string filePath, HashAlgorithmName algorithm,
        CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(algorithm);
        await using var stream = File.OpenRead(filePath);

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hasher.AppendData(buffer, 0, read);

        return Convert.ToHexString(hasher.GetHashAndReset());
    }
}
