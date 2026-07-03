using System.IO;
using System.Security.Cryptography;
using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// Verifies a downloaded file's hash against the checksum an official API (Mojang, Adoptium, Paper)
/// already returns alongside the download URL. On mismatch the file is deleted and an exception is
/// thrown, so a corrupted or tampered download is never silently trusted.
/// </summary>
public static class DownloadVerifier
{
    /// <summary>
    /// Verifies <paramref name="filePath"/> against <paramref name="expectedHex"/> using
    /// <paramref name="algorithm"/> (SHA1 or SHA256). A null/empty expected hash means the API
    /// didn't provide one for this asset; verification is skipped rather than failing the download.
    /// </summary>
    public static async Task VerifyAsync(string filePath, string? expectedHex, HashAlgorithmName algorithm,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return;

        var actualHex = await ComputeHashAsync(filePath, algorithm, ct);
        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(filePath);
            throw new InvalidOperationException(
                string.Format(Localizer.Get("Msg_ChecksumMismatchFmt"), Path.GetFileName(filePath)));
        }
    }

    private static async Task<string> ComputeHashAsync(string filePath, HashAlgorithmName algorithm, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(algorithm);
        await using var stream = File.OpenRead(filePath);

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hasher.AppendData(buffer, 0, read);

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
