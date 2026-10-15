using System;

namespace McServerLauncher.Services;

/// <summary>
/// A failure whose message was written for the user — translated, complete, ready to show.
/// </summary>
/// <remarks>
/// <para>
/// The app throws two kinds of exception. Some carry a sentence from the .resx files ("Paper has no
/// build for 1.21.9"); the rest come from .NET, the OS or a web service, in English or in the
/// system's language, naming paths and error codes. Both used to reach the screen the same way, as
/// <c>ex.Message</c>, so the second kind showed up as English text in the middle of a Spanish one.
/// </para>
/// <para>
/// Throwing this type is how code says "this message is the one to show".
/// <see cref="ErrorMessages.Describe(Exception)"/> passes it through untouched and turns everything else
/// into a sentence of its own. It derives from <see cref="InvalidOperationException"/> because that
/// is what these messages were thrown as, so every <c>catch</c> written for them still catches them.
/// </para>
/// </remarks>
public sealed class UserFacingException : InvalidOperationException
{
    public UserFacingException(string message) : base(message)
    {
    }

    public UserFacingException(string message, Exception inner) : base(message, inner)
    {
    }
}
