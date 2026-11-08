namespace McServerLauncher.Models;

/// <summary>How the side rail says that Novedades has something nobody has read yet.</summary>
/// <remarks>
/// Stored by number in settings.json, so a new style goes at the end and
/// <see cref="Number"/> stays the first: a file with no value reads as the default.
/// </remarks>
public enum NewsBadgeStyle
{
    /// <summary>How many things are unread, in a small pill over the icon.</summary>
    Number,

    /// <summary>A dot, whatever the count.</summary>
    Dot,

    /// <summary>A dot with a halo that opens out and fades, over and over.</summary>
    Pulse,

    /// <summary>Nothing on the rail: the section is still there to open.</summary>
    None,
}
