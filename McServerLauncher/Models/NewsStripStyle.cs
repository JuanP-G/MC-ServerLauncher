namespace McServerLauncher.Models;

/// <summary>Where the strip for an urgent notice (a Playit incident, a security matter) shows.</summary>
/// <remarks>
/// Stored by number in settings.json, so a new style goes at the end and
/// <see cref="Everywhere"/> stays the first: a file with no value reads as the default.
/// </remarks>
public enum NewsStripStyle
{
    /// <summary>Across the top of the window, whichever section is on screen.</summary>
    Everywhere,

    /// <summary>Only while the Tunnels section is open, where an incident of Playit is the topic.</summary>
    TunnelsOnly,

    /// <summary>Never. The notice is still in Novedades and still marks the rail.</summary>
    None,
}
