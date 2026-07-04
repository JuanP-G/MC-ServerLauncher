using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using McServerLauncher.Localization;
using McServerLauncher.Services;
using McServerLauncher.Views;

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
            desktop.MainWindow = new MainWindow();
            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// System tray icon: lets the app live in the background after the window is closed to the tray
    /// (see MainWindow.OnClosing) and offers Show/Exit. Clicking the icon restores the window; Exit is
    /// the only way to actually quit.
    /// </summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://McServerLauncher/Resources/app.ico"))),
                ToolTipText = "MC Server Launcher",
                IsVisible = true
            };
            tray.Clicked += (_, _) => RestoreMainWindow(desktop);

            var menu = new NativeMenu();
            var show = new NativeMenuItem(Localizer.Get("Tray_Show"));
            show.Click += (_, _) => RestoreMainWindow(desktop);
            var exit = new NativeMenuItem(Localizer.Get("Tray_Exit"));
            exit.Click += (_, _) =>
            {
                // The only real quit path: closing the window just hides it to the tray.
                if (desktop.MainWindow is MainWindow mw)
                    mw.RequestExit();
                else
                    desktop.MainWindow?.Close();
            };
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);
            tray.Menu = menu;

            TrayIcon.SetIcons(this, new TrayIcons { tray });
        }
        catch
        {
            // Some Linux desktops have no tray support; the app works fine without it.
        }
    }

    /// <summary>Brings the main window back from the tray (or from behind other windows).</summary>
    public static void RestoreMainWindow(IClassicDesktopStyleApplicationLifetime? desktop = null)
    {
        desktop ??= Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow is not { } w) return;
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }
}
