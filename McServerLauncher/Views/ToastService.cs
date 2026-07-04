using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace McServerLauncher.Views;

/// <summary>
/// Small toast notifications drawn as borderless always-on-top windows in the bottom-right corner
/// of the screen (pure Avalonia, no external dependencies, works even while the main window is
/// hidden in the tray). They never steal focus, auto-dismiss after a few seconds, and clicking one
/// restores the main window. Used for "player joined" / "server crashed" style events.
/// </summary>
public sealed class ToastService
{
    public static readonly ToastService Shared = new();

    private const int MaxVisible = 4;
    private const double ToastWidth = 340;
    private const double ToastHeight = 84;
    private const double Spacing = 10;
    private static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(6);

    private readonly List<Window> _visible = new();

    private ToastService() { }

    /// <summary>
    /// True when nobody is looking at the app (window hidden in the tray, minimized or just not
    /// focused) — the situations in which a toast is actually useful.
    /// </summary>
    public static bool MainWindowInactive
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d
                || d.MainWindow is not { } w)
                return true;
            return !w.IsVisible || !w.IsActive || w.WindowState == WindowState.Minimized;
        }
    }

    /// <summary>Shows a toast (thread-safe; marshals to the UI thread).</summary>
    public void Notify(string title, string message) =>
        Dispatcher.UIThread.Post(() => Show(title, message));

    private void Show(string title, string message)
    {
        try
        {
            if (_visible.Count >= MaxVisible)
            {
                var oldest = _visible[0];
                _visible.RemoveAt(0);
                oldest.Close();
            }

            var toast = BuildWindow(title, message);
            _visible.Add(toast);
            toast.Closed += (_, _) => { _visible.Remove(toast); Reposition(); };

            toast.Show();
            Reposition();

            var timer = new DispatcherTimer { Interval = AutoClose };
            timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
            timer.Start();
        }
        catch
        {
            // Toasts are best-effort; never let one break the app.
        }
    }

    private Window BuildWindow(string title, string message)
    {
        var toast = new Window
        {
            Width = ToastWidth,
            Height = ToastHeight,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false, // never steal focus from a game or another app
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
        };

        toast.Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F0252526")),
            BorderBrush = new SolidColorBrush(Color.Parse("#553FB950")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 14,
                        Foreground = Brushes.White,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = message,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };

        // Clicking a toast opens the app on the server that produced the event's console.
        toast.PointerPressed += (_, _) =>
        {
            App.RestoreMainWindow();
            toast.Close();
        };

        return toast;
    }

    /// <summary>Stacks the visible toasts upwards from the bottom-right corner of the work area.</summary>
    private void Reposition()
    {
        for (var i = 0; i < _visible.Count; i++)
        {
            var toast = _visible[i];
            var screen = toast.Screens.ScreenFromWindow(toast) ?? toast.Screens.Primary;
            if (screen is null) continue;

            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var w = (int)(ToastWidth * scale);
            var h = (int)(ToastHeight * scale);
            var m = (int)(12 * scale);
            var s = (int)(Spacing * scale);

            // Newest at the bottom; older ones pushed up.
            var slot = _visible.Count - 1 - i;
            toast.Position = new PixelPoint(
                area.Right - w - m,
                area.Bottom - h - m - slot * (h + s));
        }
    }
}
