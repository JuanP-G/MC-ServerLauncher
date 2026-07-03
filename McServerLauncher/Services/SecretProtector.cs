using System.Security.Cryptography;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Protects small secrets (like the Playit API key) at rest. On Windows the value is encrypted with
/// DPAPI scoped to the current user and stored as "dpapi:" + base64; only the same Windows user on
/// the same machine can decrypt it. On Linux/macOS .NET ships no OS keystore equivalent, so the
/// value is kept as-is there.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    /// <summary>True if <paramref name="stored"/> is a DPAPI-protected blob written by us.</summary>
    public static bool IsProtected(string? stored) =>
        stored?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Returns the protected form of <paramref name="plain"/> (idempotent). Best-effort: if DPAPI
    /// fails or the OS has no keystore, the value is returned unchanged rather than lost.
    /// </summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;
        if (!OperatingSystem.IsWindows()) return plain;

        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(bytes);
        }
        catch
        {
            return plain;
        }
    }

    /// <summary>
    /// Returns the plaintext for <paramref name="stored"/>. Unprotected values pass through
    /// unchanged (legacy settings). If the blob can't be decrypted (another user/machine, or a
    /// non-Windows OS reading a Windows blob), returns empty so the app simply asks for the key again.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        if (!OperatingSystem.IsWindows()) return string.Empty;

        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return string.Empty;
        }
    }
}
