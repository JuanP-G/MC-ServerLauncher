namespace McServerLauncher.Services;

/// <summary>
/// Unix permissions for files the app downloads and then runs: the Java runtime, the Playit agent,
/// the AppImage that replaces this app.
/// </summary>
/// <remarks>
/// Windows has no execute bit, so on Windows both methods do nothing.
/// </remarks>
internal static class FileModes
{
    /// <summary><c>rwxr-xr-x</c>: the owner can write it, everybody can run it.</summary>
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Makes <paramref name="path"/> runnable. Throws when the file cannot be changed.</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, Executable);
    }

    /// <summary>
    /// Makes <paramref name="path"/> runnable, and carries on if that is not possible. For a file
    /// whose archive usually kept the bit already: the call is a safety net, not a requirement.
    /// </summary>
    public static void TryMakeExecutable(string path)
    {
        try { MakeExecutable(path); }
        catch { /* best-effort: tar normally preserved it */ }
    }
}
