using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Protects small secrets (like the Playit API key) at rest, on every platform:
/// - Windows: DPAPI scoped to the current user, stored as "dpapi:" + base64. Only the same
///   Windows user on the same machine can decrypt it.
/// - Linux/macOS: AES-256-GCM with a random per-user key kept in a file only the user can read
///   (0600), stored as "aes:" + base64(nonce | tag | ciphertext).
/// Both protect the settings file against other users and accidental leaks (backups, sharing the
/// file); neither protects against malware already running as the same user — DPAPI can't either.
/// </summary>
public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string AesPrefix = "aes:";

    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM standard tag

    /// <summary>Key file next to settings.json (%APPDATA%/.config equivalent per platform).</summary>
    private static string KeyFilePath => AppPaths.In(".secret.key");

    /// <summary>True if <paramref name="stored"/> is a protected blob written by us.</summary>
    public static bool IsProtected(string? stored) =>
        stored?.StartsWith(DpapiPrefix, StringComparison.Ordinal) == true
        || stored?.StartsWith(AesPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Tries to protect <paramref name="plain"/> (idempotent: empty or already-protected input
    /// succeeds unchanged). Returns false when encryption fails (DPAPI unavailable, key file not
    /// writable…), leaving <paramref name="result"/> as the original plaintext so the CALLER
    /// decides what to do — a secret is never silently downgraded to plaintext on disk anymore:
    /// <see cref="AppSettingsService.Save"/> refuses to persist it and warns instead.
    /// </summary>
    public static bool TryProtect(string? plain, out string? result)
    {
        result = plain;
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return true;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);
                result = DpapiPrefix + Convert.ToBase64String(bytes);
            }
            else
            {
                result = AesPrefix + Convert.ToBase64String(EncryptAesGcm(plain));
            }
            return true;
        }
        catch
        {
            result = plain;
            return false;
        }
    }

    /// <summary>
    /// Returns the plaintext for <paramref name="stored"/>. Unprotected values pass through
    /// unchanged (legacy settings). If the blob can't be decrypted (another user/machine, missing
    /// key file, or a blob from another OS), returns empty so the app simply asks for the key again.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return string.Empty;
                var bytes = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
            }
            return DecryptAesGcm(Convert.FromBase64String(stored[AesPrefix.Length..]));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] EncryptAesGcm(string plain)
    {
        var key = GetOrCreateKey(KeyFilePath);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plainBytes.Length];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return payload;
    }

    private static string DecryptAesGcm(byte[] payload)
    {
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Payload too short.");

        var key = ReadKey(KeyFilePath) ?? throw new CryptographicException("Key file not found.");
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>The key in the file, or null if there is none, it is the wrong size, or it is being written.</summary>
    /// <remarks>
    /// Opened allowing writers: <c>File.ReadAllBytes</c> refuses a file somebody else has open for
    /// writing, which is exactly the state of a key another process is creating right now, and the
    /// exception it threw there was worse than finding nothing yet.
    /// </remarks>
    private static byte[]? ReadKey(string keyFile)
    {
        try
        {
            using var fs = new FileStream(keyFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length != KeySize) return null;

            var key = new byte[KeySize];
            fs.ReadExactly(key);
            return key;
        }
        catch (IOException)   // no such file (a subclass), or one that is still being written
        {
            return null;
        }
    }

    /// <summary>The key in <paramref name="keyFile"/>, created first if there is none. A path for the tests.</summary>
    internal static byte[] GetOrCreateKey(string keyFile)
    {
        if (ReadKey(keyFile) is { } existing) return existing;

        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        var key = RandomNumberGenerator.GetBytes(KeySize);

        // CreateNew, not Create: Create truncates, so a second process creating the key at the same
        // moment would overwrite the first one's, and everything the first had already encrypted
        // would never decrypt again. With CreateNew the loser fails and reads the winner's key.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                WriteKey(keyFile, key, FileMode.CreateNew);
                return key;
            }
            catch (IOException) when (File.Exists(keyFile))
            {
                if (ReadKey(keyFile) is { } created) return created;
                Thread.Sleep(20);   // the winner may still be writing it
            }
        }

        // Still unreadable after that: the file is damaged, not being written. Nothing it encrypted
        // can be recovered either way, so replace it, as before.
        WriteKey(keyFile, key, FileMode.Create);
        return key;
    }

    /// <summary>Writes the key readable and writable by the current user only (0600 on Unix).</summary>
    /// <remarks>On Windows this path is normally not reached, because DPAPI is used instead.</remarks>
    private static void WriteKey(string keyFile, byte[] key, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var fs = new FileStream(keyFile, options);
        fs.Write(key);
    }
}
