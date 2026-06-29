using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using McServerLauncher.Services;

namespace McServerLauncher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved language BEFORE creating the window.
        var lang = new AppSettingsService().Load().Language;
        if (!string.IsNullOrWhiteSpace(lang))
        {
            try
            {
                var ci = new CultureInfo(lang);
                CultureInfo.CurrentUICulture = ci;
                CultureInfo.DefaultThreadCurrentUICulture = ci;
            }
            catch
            {
                // Invalid language code: fall back to the system one.
            }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Placeholder window for the migration skeleton; replaced by the real MainWindow in Phase 3.
            desktop.MainWindow = new Window
            {
                Title = "MC Server Launcher",
                Width = 1150,
                Height = 720,
                Content = new TextBlock
                {
                    Text = "MC Server Launcher — Avalonia",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
