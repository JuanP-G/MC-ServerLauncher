namespace McServerLauncher.Services;

/// <summary>
/// What the window's minimize and close buttons do, as chosen in the settings. App-wide state (like
/// <see cref="NotificationPreferences.Global"/>): set at startup from the saved settings and updated
/// when the user edits them, so the change takes effect without restarting the app.
/// </summary>
public static class WindowBehavior
{
    /// <summary>Minimizing hides the window to the tray instead of leaving it in the taskbar.</summary>
    public static bool MinimizeToTray { get; set; } = true;

    /// <summary>The X button hides the window to the tray instead of quitting the app.</summary>
    public static bool CloseToTray { get; set; }
}
