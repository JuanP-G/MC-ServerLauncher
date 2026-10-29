namespace McServerLauncher.Services;

/// <summary>
/// Removing a file that is only in the way, where failing to remove it is not worth a failure of
/// its own: a half-downloaded <c>.part</c>, a package that did not verify, a temporary copy.
/// </summary>
/// <remarks>
/// Six services each had their own copy of these three lines. What they share is the decision that
/// matters, so it is written down once: an antivirus holding the file, or it being already gone, is
/// the next run's problem to clear, never this one's to report.
/// </remarks>
internal static class FileCleanup
{
    /// <summary>Deletes <paramref name="path"/> if it is there; never throws.</summary>
    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup: cleared by the next run */ }
    }
}
