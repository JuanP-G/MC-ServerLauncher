using Avalonia.Media;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The notices were toned down; a settings file written before that must follow.
/// </summary>
/// <remarks>
/// The colours are written to disk even when nobody touched them, so changing the defaults alone
/// would only have reached new installs. These hold down the rule that fixes it: exactly the old
/// default of a level becomes the new one, and anything else is somebody's choice.
/// </remarks>
public class NotificationColourMigrationTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "mcl-notifcolours-" + Guid.NewGuid().ToString("N"));

    public NotificationColourMigrationTests() => Directory.CreateDirectory(_dataDir);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp folder */ }
    }

    private static NotificationSettings OldDefaults() => new()
    {
        ColorInfo = "#2F6FB0",
        ColorSuccess = "#3FB950",
        ColorWarning = "#E3A82B",
        ColorError = "#E05561"
    };

    [Fact]
    public void TheOldDefaultsBecomeTheNewOnes()
    {
        var settings = OldDefaults();

        NotificationPalette.ModerniseColours(settings);

        foreach (var level in Enum.GetValues<NotificationLevel>())
            Assert.Equal(NotificationPalette.DefaultFor(level), settings.ColorFor(level));
    }

    [Fact]
    public void AColourSomebodyChoseIsLeftAlone()
    {
        var settings = OldDefaults();
        settings.ColorError = "#FF0000";
        settings.ColorInfo = "#123456";

        NotificationPalette.ModerniseColours(settings);

        Assert.Equal("#FF0000", settings.ColorError);
        Assert.Equal("#123456", settings.ColorInfo);
        Assert.Equal(NotificationPalette.DefaultSuccess, settings.ColorSuccess);
    }

    [Fact]
    public void TheCaseAndPaddingOfTheOldValueDoNotMatter()
    {
        var settings = new NotificationSettings { ColorSuccess = "  #3fb950 " };

        NotificationPalette.ModerniseColours(settings);

        Assert.Equal(NotificationPalette.DefaultSuccess, settings.ColorSuccess);
    }

    [Fact]
    public void RunningItTwiceChangesNothingTheSecondTime()
    {
        var settings = OldDefaults();
        settings.ColorWarning = "#ABCDEF";

        NotificationPalette.ModerniseColours(settings);
        var once = settings.Clone();
        NotificationPalette.ModerniseColours(settings);

        foreach (var level in Enum.GetValues<NotificationLevel>())
            Assert.Equal(once.ColorFor(level), settings.ColorFor(level));
    }

    [Fact]
    public void NothingToMigrateIsNotAnError()
    {
        NotificationPalette.ModerniseColours(null);
    }

    [Fact]
    public void ALoadedSettingsFileIsMoved()
    {
        var service = new AppSettingsService(_dataDir);
        var old = new AppSettings { Notifications = OldDefaults() };
        service.Save(old);

        var loaded = service.Load();

        Assert.Equal(NotificationPalette.DefaultError, loaded.Notifications.ColorError);
        Assert.Equal(NotificationPalette.DefaultSuccess, loaded.Notifications.ColorSuccess);
    }

    [Fact]
    public void ALoadedServerWithItsOwnColoursIsMovedToo()
    {
        // A server's own settings are a frozen copy of the global ones taken when its page was first
        // opened, so it holds the old defaults as well — and nothing else would ever move them.
        var storage = new ServerStorageService(_dataDir);
        var own = OldDefaults();
        own.ColorError = "#FF0000";
        storage.Save(new[]
        {
            new ServerConfig { Name = "A", UseCustomNotifications = true, Notifications = own },
            new ServerConfig { Name = "B", Notifications = null }
        });

        var loaded = storage.Load();

        Assert.Equal(NotificationPalette.DefaultSuccess, loaded[0].Notifications!.ColorSuccess);
        Assert.Equal("#FF0000", loaded[0].Notifications!.ColorError);
        Assert.Null(loaded[1].Notifications);
    }

    [Fact]
    public void TheNewDefaultsAreReadableOnTheDarkSurfaces()
    {
        // The console draws its error, warning and launcher lines with these, as text: 4.5. They
        // are also the toast's heading, which is text too.
        foreach (var level in Enum.GetValues<NotificationLevel>())
        {
            var colour = Color.Parse(NotificationPalette.DefaultFor(level));

            Assert.True(ColorPalette.ReadsWellOnDark(colour, ColorPalette.TextContrast),
                $"{level}: {NotificationPalette.DefaultFor(level)} no se lee sobre el fondo oscuro");
        }
    }

    [Fact]
    public void TheNewDefaultsAreOfferedAsSwatches()
    {
        foreach (var level in Enum.GetValues<NotificationLevel>())
            Assert.Contains(NotificationPalette.DefaultFor(level), ColorPalette.Swatches,
                StringComparer.OrdinalIgnoreCase);
    }
}
