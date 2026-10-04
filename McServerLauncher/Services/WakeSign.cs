using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// What the server list says while a server sleeps and wakes on demand: the owner's first line,
/// then a line of the launcher's own.
/// </summary>
/// <remarks>
/// Lives here, not inside the view model, because two things must agree on it exactly: the listener
/// that answers the game's ping, and the preview in the appearance editor. If the preview built the
/// notice by itself it would drift from what players see the first time somebody touched one.
/// </remarks>
internal static class WakeSign
{
    // The leading reset matters as much as the colour. Minecraft carries formatting across a line
    // break, so without it the notice inherited whatever colour the owner's MOTD happened to end
    // on — gold under one server, plain grey under the next — and read as a third line of their own
    // message instead of as the launcher speaking.

    /// <summary>Bold yellow: off, and waiting for you to do something about it.</summary>
    public const string SleepingStyle = "§r§e§l";

    /// <summary>Bold green: already on its way up, nothing to do but wait.</summary>
    public const string StartingStyle = "§r§a§l";

    /// <summary>Yellow, not bold: the disconnect screen is several lines and bold shouts.</summary>
    public const string KickStyle = "§e";

    /// <summary>The notice line, styled, in the current language.</summary>
    public static string Notice(bool starting) =>
        (starting ? StartingStyle : SleepingStyle) +
        Localizer.Get(starting ? "Wake_MotdStarting" : "Wake_MotdSleeping");

    /// <summary>Builds the two-line server-list entry: the owner's MOTD, then the notice.</summary>
    /// <param name="rawMotd">The value of <c>motd=</c> as stored: escapes and <c>§</c> codes included.</param>
    /// <param name="notice">The styled line that goes underneath, from <see cref="Notice"/>.</param>
    /// <remarks>
    /// Only the owner's FIRST line is kept. The list shows two lines and no more, so a MOTD that
    /// already uses both would push the notice off the bottom — and the notice is the one line that
    /// has to be read for any of this to work.
    /// <para>
    /// The raw value is parsed rather than split on newlines because the file never contains one:
    /// a two-line MOTD is stored as the two characters <c>\n</c>. Splitting the raw text found no
    /// break at all and let the second line through.
    /// </para>
    /// </remarks>
    public static string Compose(string? rawMotd, string notice)
    {
        var doc = MotdDocument.FromProperties(rawMotd);
        if (string.IsNullOrWhiteSpace(doc.GetText(0))) return notice;

        return doc.ToCodes(0).TrimEnd() + "\n" + notice;
    }
}
