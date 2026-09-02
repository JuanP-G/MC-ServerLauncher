using McServerLauncher.Localization;

namespace McServerLauncher.Services;

/// <summary>
/// The sign a sleeping server shows in the client's server list.
/// </summary>
/// <remarks>
/// <para>
/// One place, used by both the listener that sends it and the editor that previews it. Two copies
/// of "how the sleeping sign is built" would drift, and then the preview would promise something
/// other than what players see — which is the same trap <see cref="MotdDocument"/> and
/// <c>MinecraftMotd</c> were merged to avoid.
/// </para>
/// </remarks>
public static class WakeSign
{
    // --- How the notice looks in the server list ---
    // The leading reset matters as much as the colour. Minecraft carries formatting across a line
    // break, so without it the notice inherited whatever colour the owner's MOTD happened to end
    // on — gold under one server, plain grey under the next — and read as a third line of their own
    // message instead of as the launcher speaking.

    /// <summary>Bold yellow: off, and waiting for you to do something about it.</summary>
    private const string SleepingStyle = "§r§e§l";

    /// <summary>Bold green: already on its way up, nothing to do but wait.</summary>
    private const string StartingStyle = "§r§a§l";

    /// <summary>Yellow, not bold: the disconnect screen is several lines and bold shouts.</summary>
    public const string KickStyle = "§e";

    /// <summary>The launcher's own line, styled and translated.</summary>
    public static string Notice(bool starting) =>
        (starting ? StartingStyle : SleepingStyle) +
        Localizer.Get(starting ? "Wake_MotdStarting" : "Wake_MotdSleeping");

    /// <summary>
    /// Builds the two-line server-list entry: the owner's first line, then the notice.
    /// </summary>
    /// <param name="stored">
    /// The sign exactly as <c>server.properties</c> holds it — a two-line sign arrives here as the
    /// two characters backslash and <c>n</c>, never as a real newline.
    /// </param>
    /// <param name="notice">The launcher's line, from <see cref="Notice"/>.</param>
    /// <remarks>
    /// <para>
    /// Only the owner's FIRST line is kept. The list shows two lines and no more, so a sign that
    /// already uses both would push the notice off the bottom — and the notice is the one line that
    /// has to be read for any of this to work.
    /// </para>
    /// <para>
    /// It unescapes before splitting, and that is the whole bug this method used to have. It split
    /// on a real newline while being handed the stored form, so it never found one, kept the entire
    /// two-line sign as "the first line", and hung the notice underneath — putting a visible
    /// backslash-n in the middle of the message and the owner's real second line nowhere. Doing it
    /// here and not only at the caller is deliberate: this is the last stop before the text goes out
    /// over a socket, and the stored form is a legitimate thing to hand it.
    /// </para>
    /// </remarks>
    public static string Compose(string? stored, string notice)
    {
        if (string.IsNullOrWhiteSpace(stored)) return notice;

        var first = MotdDocument.Unescape(stored).Split((char)10, (char)13)[0].TrimEnd();
        return first.Length == 0 ? notice : first + (char)10 + notice;
    }

    /// <summary>The whole sign for a given state, ready to send or to preview.</summary>
    public static string For(string? stored, bool starting) => Compose(stored, Notice(starting));
}
