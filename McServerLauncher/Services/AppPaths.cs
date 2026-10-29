namespace McServerLauncher.Services;

/// <summary>
/// Where the app keeps what it writes about itself: settings, logs, caches, the Java runtimes it
/// downloads, the Playit agent. One place, so a change to where that is (a portable mode, a test
/// folder) is a change to one line instead of to nineteen.
/// </summary>
/// <remarks>
/// <para>
/// This is the app's own folder under the user's application data (<c>%APPDATA%</c> on Windows,
/// <c>~/.config</c> on Linux, <c>~/Library/Application Support</c> on macOS). The folders of the
/// servers themselves are not here: the user picks those, and <see cref="ServerStorageService"/>
/// remembers them.
/// </para>
/// <para>
/// The path is read on every call rather than kept in a static field: it is cheap, and a test that
/// points <c>APPDATA</c> somewhere else after the type is loaded still gets what it asked for.
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>The folder name under the user's application data.</summary>
    internal const string FolderName = "McServerLauncher";

    /// <summary>The app's own data folder. Not created: whoever writes into it creates it.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>A path inside <see cref="DataDir"/>, built from <paramref name="parts"/>.</summary>
    public static string In(params string[] parts) =>
        Path.Combine([DataDir, .. parts]);
}
