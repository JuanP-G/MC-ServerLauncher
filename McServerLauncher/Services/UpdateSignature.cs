using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Checks that an update package was signed by whoever publishes this project, with a key that
/// does not live on GitHub — and that the signature is for <em>this</em> version of <em>this</em>
/// package.
/// </summary>
/// <remarks>
/// <para>
/// The checksum next to a package comes from the same release as the package, so it only catches a
/// damaged download: whoever can upload the one can upload the other. This is the half that does
/// not depend on GitHub being safe. The private key stays on the maintainer's machine; what is here
/// is only the public half, and a package without a signature made by it is not installed.
/// </para>
/// <para>
/// What is signed is not the package alone but a short message that also names the version and the
/// file (see <see cref="SignedMessage"/>). Signing only the bytes would leave an old release, signed
/// correctly in its day, valid for ever: somebody able to publish could offer it again under a newer
/// tag, and it would pass. With the version inside the message, the signature of 1.21.1 does not
/// verify as 1.99.0, and the .dmg's does not verify as the .exe's.
/// </para>
/// <para>
/// ECDSA over P-256 with SHA-256, because it is what .NET ships (so the update path pulls in no
/// package of its own) and what <c>openssl</c> produces. The <c>.sig2</c> asset is the DER signature,
/// written in base64 so it is plain text.
/// </para>
/// <para>
/// Several keys are trusted at once so one can be replaced without stranding anyone: a release
/// signed with the new key ships in an app that already trusts it, and the old key is dropped a
/// version later. See <c>docs/articles/signing.md</c>.
/// </para>
/// </remarks>
public static class UpdateSignature
{
    /// <summary>First line of the signed message: the format, so a later one cannot be mistaken for it.</summary>
    internal const string MessageHeader = "mcsl-update-v1";

    /// <summary>
    /// Public keys (SubjectPublicKeyInfo, base64) whose signatures are accepted. The first one is the
    /// current key; the rest are for a rotation in progress.
    /// </summary>
    /// <remarks>
    /// Empty would mean no update can be installed from inside the app — the safe failure, but one a
    /// release must never ship with; a test pins that the list holds at least one usable P-256 key.
    /// </remarks>
    internal static readonly IReadOnlyList<string> TrustedKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDy6PYAuIYV9i1Z5tLuFY1uEWgt+DURiQqzLq8Ltw7KLnuXRpprTroUkmI8KpJbl0YF3bdHd5Rcm33fG+5u2GSw=="
    ];

    /// <summary>
    /// The bytes that are signed: the format, the version, the file name and the package's SHA-256,
    /// one per line, joined by <c>\n</c> with nothing after the last.
    /// </summary>
    /// <remarks>
    /// The signing script builds the same text, so any change here has to be made there too — and
    /// to the header, or a signature of the old layout could be read as the new one.
    /// </remarks>
    internal static byte[] SignedMessage(string version, string fileName, string sha256Hex) =>
        Encoding.UTF8.GetBytes(
            string.Join('\n', MessageHeader, version, fileName, sha256Hex.ToLowerInvariant()));

    /// <summary>
    /// Whether <paramref name="signatureBase64"/> is a valid signature, by any of
    /// <paramref name="keys"/> (the built-in ones by default), of the file at
    /// <paramref name="packagePath"/> as <paramref name="version"/>'s <paramref name="fileName"/>.
    /// </summary>
    /// <remarks>
    /// Anything wrong with the input — bad base64, a signature of the wrong shape, a key that is not
    /// P-256 — is "not valid", never an exception: the caller's answer is the same either way, and a
    /// crash here would be a way to skip the check by feeding it garbage.
    /// </remarks>
    public static bool Verify(string packagePath, string version, string fileName,
        string? signatureBase64, IEnumerable<string>? keys = null)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(fileName)) return false;

        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }

        string hash;
        using (var stream = File.OpenRead(packagePath))
            hash = Convert.ToHexString(SHA256.HashData(stream));
        var message = SignedMessage(version.Trim(), Path.GetFileName(fileName), hash);

        foreach (var key in keys ?? TrustedKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.KeySize != 256) continue;

                if (ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256,
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
