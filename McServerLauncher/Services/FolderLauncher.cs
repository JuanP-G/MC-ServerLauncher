using System.Diagnostics;
using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// Opens a folder in the system's file manager.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="BrowserLauncher"/> on purpose, and not a relaxation of it.
/// <c>BrowserLauncher</c> refuses anything that is not an absolute http(s) URL <i>because</i> the
/// links it opens come from Modrinth — somebody else's text — and <c>UseShellExecute</c> would
/// happily run a local executable or a registered custom scheme. Widening that check to let a path
/// through would have removed the guard for every caller, including the ones that need it.
/// </para>
/// <para>
/// This one has the opposite problem and so gets the opposite check: the path is ours (it is the
/// server's own folder from the config), but it still has to be a folder that <i>exists</i>, or the
/// shell is being handed a string to interpret. A file is refused as well as a missing path: opening
/// a <c>.bat</c> or a <c>.jar</c> through the shell runs it.
/// </para>
/// </remarks>
public static class FolderLauncher
{
    /// <summary>True when this is a real, existing directory and safe to hand to the shell.</summary>
    public static bool IsOpenableFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <summary>Opens the folder. Anything that is not one is ignored, silently and on purpose.</summary>
    public static void Open(string? path)
    {
        if (!IsOpenableFolder(path)) return;

        try
        {
            // UseShellExecute with a directory asks the desktop to show it — Explorer on Windows,
            // Finder or the user's file manager elsewhere — rather than executing anything.
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // No file manager registered, or the shell refused. Nothing useful to do about it, and
            // certainly nothing worth interrupting the user for.
        }
    }
}
