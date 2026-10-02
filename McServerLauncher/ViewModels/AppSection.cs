namespace McServerLauncher.ViewModels;

/// <summary>
/// What fills the window next to the rail.
/// </summary>
/// <remarks>
/// Settings is not here on purpose: it is a dialog with Cancel and Save, and turning it into a
/// section would mean it saved on every click. The rail has a button for it all the same.
/// </remarks>
public enum AppSection
{
    Servers,
    Tunnels,
    About,
}
