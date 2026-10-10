using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The language is read at start-up without touching anything else in settings.json.
/// </summary>
/// <remarks>
/// The app read the whole settings file for it — decrypting the Playit keys and migrating an older
/// file — just before the main view model did the same again.
/// </remarks>
public class SettingsLanguageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcl-lang-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheLanguageIsReadAndTheFileLeftAsItWas()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        // A plaintext key, as an older version wrote it: a full Load would migrate and rewrite this.
        File.WriteAllText(path, "{ \"Language\": \"de\", \"PlayitApiKey\": \"plain\" }");
        var before = File.ReadAllText(path);

        Assert.Equal("de", new AppSettingsService(_dir).LoadLanguage());
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void NoFileMeansNoLanguage() =>
        Assert.Null(new AppSettingsService(_dir).LoadLanguage());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
