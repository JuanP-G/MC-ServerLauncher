using System.IO;
using System.Text.Json;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>Loads and saves the global settings in %APPDATA%\McServerLauncher\settings.json.</summary>
public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dataDir;
    private readonly string _filePath;

    /// <summary>Default constructor uses %APPDATA%; <paramref name="dataDir"/> is for tests.</summary>
    public AppSettingsService(string? dataDir = null)
    {
        _dataDir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher");
        _filePath = Path.Combine(_dataDir, "settings.json");
    }

    /// <summary>
    /// True when the last <see cref="Save"/> could not encrypt the Playit key: the key was NOT
    /// written to disk (a secret is never persisted in plaintext) and keeps working only for this
    /// session. The failure is also recorded in the daily log; the UI shows a one-time warning.
    /// </summary>
    public bool LastSaveCouldNotProtectKey { get; private set; }

    public AppSettings Load()
    {
        try
        {
            // Corrupt file → the last good ".bak" copy is restored automatically; if there is none,
            // falling back to defaults is fine for settings (nothing irreplaceable lives here).
            var (loaded, _) = AtomicJsonFile.Load<AppSettings>(_filePath, JsonOptions);
            var settings = loaded ?? new AppSettings();

            // Playit secrets are stored encrypted (DPAPI on Windows, AES elsewhere); callers always
            // see the plaintext. A plaintext secret written by an older version is migrated
            // (re-saved encrypted) right away — but only if encryption actually works right now:
            // re-saving while it's broken would drop the secret from a file that already contained
            // it in plaintext, i.e. destroy data without making anything safer.
            var needsMigration = false;
            settings.PlayitApiKey = DecryptTracking(settings.PlayitApiKey, ref needsMigration);
            settings.PlayitAgentSecretKey = DecryptTracking(settings.PlayitAgentSecretKey, ref needsMigration);

            if (needsMigration)
            {
                if (SecretProtector.TryProtect(settings.PlayitApiKey, out _) &&
                    SecretProtector.TryProtect(settings.PlayitAgentSecretKey, out _))
                    Save(settings);
                else
                    ConsoleLogService.Shared.Log("Launcher",
                        "Could not encrypt a legacy plaintext Playit secret (protection unavailable); leaving settings.json unchanged.");
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_dataDir);

        // Write a copy with the secrets protected, so the caller's instance keeps the usable
        // plaintext. If encryption fails (DPAPI unavailable, key file not writable…), REFUSE to
        // persist that secret rather than silently downgrading it to plaintext on disk (SEG-4): the
        // in-memory session keeps working and the app will simply ask for it again next time.
        var legacyOk = SecretProtector.TryProtect(settings.PlayitApiKey, out var protectedKey);
        var agentOk = SecretProtector.TryProtect(settings.PlayitAgentSecretKey, out var protectedAgent);
        LastSaveCouldNotProtectKey = !legacyOk || !agentOk;
        if (!legacyOk) protectedKey = string.Empty;
        if (!agentOk) protectedAgent = string.Empty;
        if (LastSaveCouldNotProtectKey)
            ConsoleLogService.Shared.Log("Launcher",
                "Could not encrypt a Playit secret (DPAPI/key-file failure); it was NOT saved to disk and will be asked for again.");

        var toWrite = new AppSettings
        {
            PlayitApiKey = protectedKey,
            PlayitAgentSecretKey = protectedAgent,
            PlayitAgentId = settings.PlayitAgentId,
            Language = settings.Language,
            LastVersionSeen = settings.LastVersionSeen,
            Notifications = settings.Notifications
        };
        AtomicJsonFile.Write(_filePath, toWrite, JsonOptions);
    }

    /// <summary>Decrypts a stored secret; flags <paramref name="needsMigration"/> if it was plaintext.</summary>
    private static string? DecryptTracking(string? stored, ref bool needsMigration)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!SecretProtector.IsProtected(stored)) needsMigration = true;
        return SecretProtector.Unprotect(stored);
    }
}
