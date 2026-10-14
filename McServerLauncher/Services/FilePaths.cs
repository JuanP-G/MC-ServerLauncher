namespace McServerLauncher.Services;

/// <summary>Whether two paths name the same file, the way the system the app runs on decides it.</summary>
/// <remarks>
/// Windows and macOS (APFS, by default) ignore case in file names; Linux does not. Comparing with
/// case ignored everywhere took <c>Sodium.jar</c> for <c>sodium.jar</c> on Linux, where they are
/// two files — and the old copy of a mod stayed beside the new one.
/// </remarks>
internal static class FilePaths
{
    public static StringComparison Comparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same file.</summary>
    public static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Comparison);
}
