using System;

namespace McServerLauncher.Services;

/// <summary>
/// For the failures the app decides to carry on after: they are not shown to the user, but they are
/// not thrown away either.
/// </summary>
/// <remarks>
/// A <c>catch</c> that is empty makes a real fault invisible: a cache that never loads, a port check
/// that could not run, a download that skipped its checksum. Each of those is the right thing to
/// survive, and each is something somebody looking at a bug report wants to find in the log. The
/// message goes to the day's log, through <see cref="ErrorMessages.Describe(Exception, Action{string})"/>
/// so it carries what the user would have been told and the detail behind it.
/// </remarks>
public static class Safe
{
    /// <summary>Writes a survived failure to the log. Never throws.</summary>
    /// <param name="area">The log's tag: who the line is about (Players, Update, Store...).</param>
    /// <param name="what">What was being tried, in a few words, and what the app did instead.</param>
    /// <param name="error">The failure that was survived.</param>
    public static void Note(string area, string what, Exception error)
    {
        try
        {
            ErrorMessages.Describe(error, details => ConsoleLogService.Shared.Log(area, what + ": " + details));
        }
        catch
        {
            // The log is the one place a failure may not be reported: it is how failures are reported.
        }
    }
}
