using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

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

    /// <summary>
    /// Shows a toast (thread-safe; marshals to the UI thread).
    /// </summary>
    /// <remarks>
    /// The level and the mark are passed in rather than worked out here, because what a toast means
    /// is not something the window that draws it can know. Every toast used to look identical — the
    /// same panel with the same faint green border whether somebody had joined or the server had
    /// died — so the only way to tell them apart was to stop and read.
    /// </remarks>
    public void Notify(string title, string message,
        NotificationLevel level = NotificationLevel.Info,
        string emoji = "",
        NotificationSettings? colours = null) =>
        Dispatcher.UIThread.Post(() => Show(title, message, level, emoji, colours));

    private void Show(string title, string message, NotificationLevel level, string emoji,
        NotificationSettings? colours)
    {
        try
        {
            if (_visible.Count >= MaxVisible)
            {
                var oldest = _visible[0];
                _visible.RemoveAt(0);
                oldest.Close();
            }

            var toast = BuildWindow(title, message, level, emoji, colours);
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

    private Window BuildWindow(string title, string message, NotificationLevel level, string emoji,
        NotificationSettings? colours)
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

        var accent = NotificationBrushes.BrushFor(colours, level);

        var text = new StackPanel
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
        };

        // Three marks of the same thing, on purpose. The stripe down the left edge is what the eye
        // catches from across a room; the emoji is what still works for somebody who cannot tell
        // this red from this green; the border ties it together. Colour alone would carry none of
        // it to a third of a percent of players, and shape alone would be quieter than it needs to.
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            Children =
            {
                new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = accent },
                new TextBlock
                {
                    Text = emoji,
                    FontSize = 20,
                    Margin = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsVisible = !string.IsNullOrEmpty(emoji)
                },
                text
            }
        };

        Grid.SetColumn(body.Children[1], 1);
        Grid.SetColumn(body.Children[2], 2);

        toast.Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F0252526")),
            BorderBrush = NotificationBrushes.FadedBrushFor(colours, level),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = body
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
