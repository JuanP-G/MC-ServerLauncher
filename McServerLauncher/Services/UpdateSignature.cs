using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace McServerLauncher.Services;

/// <summary>
/// Checks that an update package was signed by whoever publishes this project, with a key that
/// does not live on GitHub.
/// </summary>
/// <remarks>
/// <para>
/// The checksum next to a package comes from the same release as the package, so it only catches a
/// damaged download: whoever can upload the one can upload the other. This is the half that does
/// not depend on GitHub being safe. The private key stays on the maintainer's machine; what is here
/// is only the public half, and a package without a signature made by it is not installed.
/// </para>
/// <para>
/// ECDSA over P-256 with SHA-256, because it is what .NET ships (so the update path pulls in no
/// package of its own) and what <c>openssl</c> and <c>cosign</c> produce. The <c>.sig</c> asset is
/// the DER signature of the package bytes, written in base64 so it is plain text.
/// </para>
/// <para>
/// Several keys are trusted at once so one can be replaced without stranding anyone: a release
/// signed with the new key ships in an app that already trusts it, and the old key is dropped a
/// version later. See <c>docs/articles/signing.md</c>.
/// </para>
/// </remarks>
public static class UpdateSignature
{
    /// <summary>
    /// Public keys (SubjectPublicKeyInfo, base64) whose signatures are accepted. The first one is the
    /// current key; the rest are for a rotation in progress.
    /// </summary>
    /// <remarks>
    /// Empty means no update can be installed from inside the app — the safe failure, and the reason
    /// a build made before the first key existed falls back to the release page.
    /// </remarks>
    internal static readonly IReadOnlyList<string> TrustedKeys = [];

    /// <summary>
    /// Whether <paramref name="signatureBase64"/> is a valid signature of the file at
    /// <paramref name="packagePath"/> by any of <paramref name="keys"/> (the built-in ones by default).
    /// </summary>
    /// <remarks>
    /// Anything wrong with the input — bad base64, a signature of the wrong shape, a key that is not
    /// P-256 — is "not valid", never an exception: the caller's answer is the same either way, and a
    /// crash here would be a way to skip the check by feeding it garbage.
    /// </remarks>
    public static bool Verify(string packagePath, string? signatureBase64,
        IEnumerable<string>? keys = null)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;

        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }

        foreach (var key in keys ?? TrustedKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.KeySize != 256) continue;

                using var stream = File.OpenRead(packagePath);
                if (ecdsa.VerifyData(stream, signature, HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence))
                    return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // This key could not check it; another may, and if none can the answer is "no".
            }
        }
        return false;
    }
}
