using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace McServerLauncher.Services;

/// <summary>The windows the app has, for whatever needs one to own a dialog or reach the clipboard.</summary>
/// <remarks>
/// "The main window" was looked up in seven places, each spelling out the lifetime cast.
/// </remarks>
public static class AppWindows
{
    /// <summary>The main window, or null before it exists and in a test.</summary>
    public static Window? Main =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
