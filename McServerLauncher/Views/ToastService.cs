using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views;

/// <summary>
/// Small toast notifications drawn as borderless always-on-top windows in the bottom-right corner
/// of the screen (pure Avalonia, no external dependencies, works even while the main window is
/// hidden in the tray). They never steal focus, close by themselves after a few seconds — a bar
/// along the bottom shows how many are left, and the pointer resting on a card stops the clock —
/// and clicking one restores the main window. Used for "player joined" / "server crashed" style
/// events.
/// </summary>
public sealed class ToastService
{
    public static readonly ToastService Shared = new();

    private const int MaxVisible = 4;
    private const double ToastWidth = 360;
    private const double ToastHeight = 88;
    private const double Spacing = 10;
    private const double BarHeight = 2;
    private const double RingSize = 38;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    // The card is the same dark neutral whatever the event; only the ring, the heading and the bar
    // carry the level's colour. These are not the user's to configure.
    private static readonly IBrush CardBackground = new SolidColorBrush(Color.Parse("#F0252526"));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#3A3A3D"));
    private static readonly IBrush MessageText = new SolidColorBrush(Color.Parse("#ECECEE"));
    private static readonly IBrush QuietText = new SolidColorBrush(Color.Parse("#85858C"));

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
    /// <param name="heading">What happened, in a few words: the coloured line at the top.</param>
    /// <param name="source">Where it came from — the server's name — kept small beside the time.</param>
    /// <param name="message">The detail.</param>
    /// <param name="icon">The mark inside the ring.</param>
    /// <param name="level">How serious it is, which decides the colour and how long it stays.</param>
    /// <param name="colours">The colours to draw the level with; the defaults when null.</param>
    /// <remarks>
    /// The level and the mark are passed in rather than worked out here, because what a toast means
    /// is not something the window that draws it can know. Every toast used to look identical — the
    /// same panel with the same faint green border whether somebody had joined or the server had
    /// died — so the only way to tell them apart was to stop and read.
    /// </remarks>
    public void Notify(string heading, string source, string message, Symbol icon,
        NotificationLevel level = NotificationLevel.Info,
        NotificationSettings? colours = null) =>
        Dispatcher.UIThread.Post(() => Show(heading, source, message, icon, level, colours));

    private void Show(string heading, string source, string message, Symbol icon,
        NotificationLevel level, NotificationSettings? colours)
    {
        try
        {
            if (_visible.Count >= MaxVisible)
            {
                var oldest = _visible[0];
                _visible.RemoveAt(0);
                oldest.Close();
            }

            var life = new ToastLifetime(level);
            var bar = new Border
            {
                Height = BarHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = NotificationBrushes.BrushFor(colours, level)
            };

            var toast = BuildWindow(heading, source, message, icon, level, colours, bar);
            toast.Opacity = 0;
            _visible.Add(toast);

            // One clock per toast, ticking until the window goes — whichever way it goes. The timer
            // used to be a one-shot nothing could stop, so a toast closed by a click left it running.
            var timer = new DispatcherTimer { Interval = Frame };
            var stopwatch = Stopwatch.StartNew();
            var last = TimeSpan.Zero;
            timer.Tick += (_, _) =>
            {
                var now = stopwatch.Elapsed;
                life.Advance(now - last);
                last = now;

                bar.Width = (ToastWidth - 2) * life.Remaining;
                toast.Opacity = life.Opacity;

                if (life.Expired) toast.Close();
            };

            toast.PointerEntered += (_, _) => life.Paused = true;
            toast.PointerExited += (_, _) => life.Paused = false;
            toast.Closed += (_, _) =>
            {
                timer.Stop();
                _visible.Remove(toast);
                Reposition();
            };

            bar.Width = ToastWidth - 2;
            toast.Show();
            Reposition();
            timer.Start();
        }
        catch
        {
            // Toasts are best-effort; never let one break the app.
        }
    }

    private static Window BuildWindow(string heading, string source, string message, Symbol icon,
        NotificationLevel level, NotificationSettings? colours, Border bar)
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

        // The ring is what the eye lands on: the one shape on the card, and the one place the colour
        // is drawn round and not as text. It still has to work for somebody who cannot tell this red
        // from this green, which is what the icon inside it is for — no two kinds share one.
        var ring = new Border
        {
            Width = RingSize,
            Height = RingSize,
            CornerRadius = new CornerRadius(RingSize / 2),
            BorderThickness = new Thickness(1.5),
            BorderBrush = NotificationBrushes.FadedBrushFor(colours, level, 0x88),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new SymbolIcon
            {
                Symbol = icon,
                FontSize = 19,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // What happened leads and carries the colour; where it happened is small and grey on the
        // other side of the line, beside the time. With a handful of servers the name is a detail;
        // it does not need to out-shout the event.
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                new TextBlock
                {
                    Text = heading,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = accent,
                    VerticalAlignment = VerticalAlignment.Center
                },
                // The name gives way, never the time: a long server name is cut with an ellipsis and
                // the hour beside it stays whole.
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = source,
                            FontSize = 11,
                            Foreground = QuietText,
                            TextAlignment = TextAlignment.Right,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = " · " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture),
                            FontSize = 11,
                            Foreground = QuietText
                        }
                    }
                }
            }
        };
        var when = (Grid)header.Children[1];
        Grid.SetColumn(when, 1);
        Grid.SetColumn(when.Children[1], 1);

        var text = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                header,
                new TextBlock
                {
                    Text = message,
                    FontSize = 14,
                    Foreground = MessageText,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(14, 12, 14, 14),
            Children = { ring, text }
        };
        text.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(text, 1);

        toast.Content = new Border
        {
            Background = CardBackground,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new Grid { Children = { body, bar } }
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
