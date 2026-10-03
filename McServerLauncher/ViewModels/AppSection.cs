namespace McServerLauncher.ViewModels;

/// <summary>
/// What fills the window next to the rail.
/// </summary>
/// <remarks>
/// Settings joined the others once it learned to save as it goes; while it was a dialog with Cancel
/// and Save it could not be a screen you simply switch away from.
/// </remarks>
public enum AppSection
{
    Servers,
    Tunnels,
    Settings,
    About,
}
