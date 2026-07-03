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

    public AppSettingsService()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerLauncher");
        _filePath = Path.Combine(_dataDir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? new AppSettings();

            // The Playit key is stored DPAPI-protected on Windows; callers always see the plaintext.
            // A plaintext key written by an older version is migrated (re-saved encrypted) right away.
            if (!string.IsNullOrEmpty(settings.PlayitApiKey))
            {
                var wasProtected = SecretProtector.IsProtected(settings.PlayitApiKey);
                settings.PlayitApiKey = SecretProtector.Unprotect(settings.PlayitApiKey);
                if (!wasProtected && OperatingSystem.IsWindows())
                    Save(settings);
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
        // Write a copy with the key protected, so the caller's instance keeps the usable plaintext.
        var toWrite = new AppSettings
        {
            PlayitApiKey = string.IsNullOrEmpty(settings.PlayitApiKey)
                ? settings.PlayitApiKey
                : SecretProtector.Protect(settings.PlayitApiKey),
            Language = settings.Language,
            LastVersionSeen = settings.LastVersionSeen
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(toWrite, JsonOptions));
    }
}
