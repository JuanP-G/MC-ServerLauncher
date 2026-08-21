using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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

            // Launching the app again brings this window back rather than opening a second copy.
            // The event arrives on a pipe-listener thread, so it has to hop to the UI thread before
            // touching the window.
            if (Program.Instance is { } instance)
                instance.ActivationRequested += () => Dispatcher.UIThread.Post(() => RestoreMainWindow(desktop));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// True once the tray icon exists. MainWindow only hides itself on minimize when this holds:
    /// without a tray there would be no way to bring the window back.
    /// </summary>
    public static bool TrayAvailable { get; private set; }

    /// <summary>
    /// System tray icon: lets the app live in the background while minimized to the tray (see
    /// MainWindow) and offers Show/Exit. Clicking the icon restores the window.
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
                // Same clean-shutdown path the window's X button uses.
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
            TrayAvailable = true;
        }
        catch
        {
            // Some Linux desktops have no tray support; the app works fine without it.
        }
    }

    /// <summary>Brings the main window back from the tray (or from behind other windows).</summary>
    /// <remarks>
    /// The Topmost flick is not decoration. Windows refuses SetForegroundWindow to a process that
    /// isn't already in front, so when the request comes from a second launch of the app (see
    /// <see cref="Services.SingleInstance"/>) a plain Activate() only flashes the taskbar button.
    /// Briefly making the window topmost puts it in front for real; it is put back immediately so
    /// the window doesn't end up permanently hovering over everything else.
    /// </remarks>
    public static void RestoreMainWindow(IClassicDesktopStyleApplicationLifetime? desktop = null)
    {
        desktop ??= Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow is not { } w) return;

        // Normal first, then Show — not the other way round. Mapping a window that is still flagged
        // minimized is the same mistake that broke minimize-to-tray on Linux (see MainWindow): the
        // frame comes back but its contents are never redrawn.
        w.WindowState = WindowState.Normal;
        w.Show();

        var wasTopmost = w.Topmost;
        w.Topmost = true;
        w.Activate();

        // Long enough for the window manager to have acted on it. Posting the revert at Background
        // priority instead ran it inside the same frame, which put the window back down before
        // anything had a chance to raise it.
        DispatcherTimer.RunOnce(() => w.Topmost = wasTopmost, TimeSpan.FromMilliseconds(400));
    }
}
